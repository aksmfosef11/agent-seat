using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

return await AgentSeatCli.RunAsync(args);

internal static class AgentSeatCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        var arguments = args.ToList();
        var url = TakeOption(arguments, "--url") ?? "http://127.0.0.1:38399";
        var actionToken = TakeOption(arguments, "--action-token") ??
                          Environment.GetEnvironmentVariable("AGENTSEAT_ACTION_TOKEN");
        if (arguments.Count == 0 || arguments[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"{url.TrimEnd('/')}/api/v1/"),
            // Starting an agent seat waits for its Windows session, and batches can include waits.
            Timeout = TimeSpan.FromSeconds(string.Equals(arguments[0], "computer", StringComparison.OrdinalIgnoreCase) ? 180 : 35)
        };

        try
        {
            return arguments[0].ToLowerInvariant() switch
            {
                "status" => await PrintJsonAsync(client, "health"),
                "list" => await ListSeatsAsync(client),
                "sessions" => await PrintJsonAsync(client, "sessions"),
                "preflight" => await PreflightAsync(client),
                "add" => await AddSeatAsync(client, arguments),
                "remove" => await RemoveSeatAsync(client, arguments),
                "rdp" => await DownloadRdpAsync(client, arguments),
                "stream-status" => await StreamStatusAsync(client, arguments),
                "stream-start" => await StreamStartAsync(client, arguments, actionToken),
                "stream-stop" => await StreamStopAsync(client, arguments, actionToken),
                "pair" => await PairAsync(client, arguments, actionToken),
                "disconnect" => await SessionActionAsync(client, arguments, "disconnect", actionToken),
                "logoff" => await SessionActionAsync(client, arguments, "logoff", actionToken),
                "agent-control" => await AgentControlAsync(client, arguments, actionToken),
                "computer" => await ComputerCli.RunAsync(client, arguments, actionToken),
                _ => UnknownCommand(arguments[0])
            };
        }
        catch (HttpRequestException exception)
        {
            Console.Error.WriteLine($"AgentSeat service request failed: {exception.Message}");
            return 1;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("AgentSeat service request timed out.");
            return 1;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<int> ListSeatsAsync(HttpClient client)
    {
        using var document = await GetJsonAsync(client, "seats");
        var seats = document.RootElement;
        if (seats.GetArrayLength() == 0)
        {
            Console.WriteLine("No seats configured.");
            return 0;
        }

        foreach (var view in seats.EnumerateArray())
        {
            var seat = view.GetProperty("seat");
            var sessions = view.GetProperty("sessions");
            var online = view.GetProperty("online").GetBoolean();
            var streaming = view.GetProperty("streaming");
            Console.WriteLine(
                $"{seat.GetProperty("id").GetString(),-18} " +
                $"{seat.GetProperty("userName").GetString(),-24} " +
                $"{(online ? "online" : "offline"),-8} " +
                $"stream={streaming.GetProperty("state").GetString(),-15} " +
                $"sessions={sessions.GetArrayLength()} " +
                $"moonlight={streaming.GetProperty("moonlightAddress").GetString()}");
        }

        return 0;
    }

    private static async Task<int> PreflightAsync(HttpClient client)
    {
        using var document = await GetJsonAsync(client, "preflight");
        var root = document.RootElement;
        Console.WriteLine($"Host: {root.GetProperty("hostName").GetString()}");
        Console.WriteLine($"Ready: {root.GetProperty("ready").GetBoolean()}");
        foreach (var check in root.GetProperty("checks").EnumerateArray())
        {
            var status = check.GetProperty("status").GetString()?.ToUpperInvariant();
            Console.WriteLine($"[{status,-7}] {check.GetProperty("title").GetString()}");
            Console.WriteLine($"          {check.GetProperty("detail").GetString()}");
        }

        return root.GetProperty("ready").GetBoolean() ? 0 : 3;
    }

    private static async Task<int> AddSeatAsync(HttpClient client, List<string> arguments)
    {
        if (arguments.Count < 3)
        {
            throw new ArgumentException(
                "Usage: agent-seat add <id> <windows-user> [--name NAME] [--host HOST] [--port PORT] [--sunshine-port PORT] [--width PX] [--height PX]");
        }

        var id = arguments[1];
        var user = arguments[2];
        var agent = TakeFlag(arguments, "--agent");
        var name = TakeOption(arguments, "--name") ?? id;
        var host = TakeOption(arguments, "--host") ?? Environment.MachineName;
        var port = ParseIntOption(arguments, "--port", 3389);
        // 1280x800 stays below the size at which vision models start resizing screenshots, so the
        // coordinates an AI reads off an image are exactly the desktop's pixels.
        var width = ParseIntOption(arguments, "--width", agent ? 1280 : 1920);
        var height = ParseIntOption(arguments, "--height", agent ? 800 : 1080);
        var sunshineBasePort = ParseIntOption(arguments, "--sunshine-port", 0);
        var payload = new
        {
            id,
            displayName = name,
            userName = user,
            hostAddress = host,
            rdpPort = port,
            width,
            height,
            fullScreen = !arguments.Contains("--windowed", StringComparer.OrdinalIgnoreCase),
            playAudioOnClient = true,
            redirectClipboard = arguments.Contains("--clipboard", StringComparer.OrdinalIgnoreCase),
            streamingEnabled = !agent,
            autoStartStreaming = !agent,
            agentControlEnabled = agent,
            sunshineBasePort,
            enabled = true
        };

        using var response = await client.PostAsJsonAsync("seats", payload, JsonOptions);
        await EnsureSuccessAsync(response);
        Console.WriteLine(agent
            ? $"Created agent seat '{id}'. Install its Windows account and anchor with scripts\\Install-DuoSeat.ps1 -AgentSeat, then run: agent-seat computer guide"
            : $"Created seat '{id}'.");
        return 0;
    }

    private static async Task<int> AgentControlAsync(
        HttpClient client,
        IReadOnlyList<string> arguments,
        string? actionToken)
    {
        RequireCount(arguments, 3, "Usage: agent-seat agent-control <seat-id> <on|off>");
        var enabled = arguments[2].ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => throw new ArgumentException("Usage: agent-seat agent-control <seat-id> <on|off>")
        };
        using var response = await client.PutAsJsonAsync(
            $"seats/{Uri.EscapeDataString(arguments[1])}/agent-control",
            new { enabled, actionToken },
            JsonOptions);
        await EnsureSuccessAsync(response);
        Console.WriteLine($"Agent control for seat '{arguments[1]}' is now {(enabled ? "ON" : "OFF")}.");
        return 0;
    }

    private static async Task<int> RemoveSeatAsync(HttpClient client, IReadOnlyList<string> arguments)
    {
        RequireCount(arguments, 2, "Usage: agent-seat remove <id> --yes");
        RequireConfirmation(arguments);
        using var response = await client.DeleteAsync($"seats/{Uri.EscapeDataString(arguments[1])}");
        await EnsureSuccessAsync(response);
        Console.WriteLine($"Removed seat '{arguments[1]}'. The Windows account was not deleted.");
        return 0;
    }

    private static async Task<int> DownloadRdpAsync(HttpClient client, IReadOnlyList<string> arguments)
    {
        RequireCount(arguments, 2, "Usage: agent-seat rdp <id> [output-file]");
        var outputPath = Path.GetFullPath(arguments.Count >= 3 ? arguments[2] : $"agent-seat-{arguments[1]}.rdp");
        using var response = await client.GetAsync($"seats/{Uri.EscapeDataString(arguments[1])}/rdp");
        await EnsureSuccessAsync(response);
        await File.WriteAllBytesAsync(outputPath, await response.Content.ReadAsByteArrayAsync());
        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    private static Task<int> StreamStatusAsync(HttpClient client, IReadOnlyList<string> arguments)
    {
        RequireCount(arguments, 2, "Usage: agent-seat stream-status <seat-id>");
        return PrintJsonAsync(client, $"seats/{Uri.EscapeDataString(arguments[1])}/stream");
    }

    private static async Task<int> StreamStartAsync(
        HttpClient client,
        IReadOnlyList<string> arguments,
        string? actionToken)
    {
        RequireCount(arguments, 3, "Usage: agent-seat stream-start <seat-id> <session-id> --yes");
        RequireConfirmation(arguments);
        RequireActionToken(actionToken);
        if (!int.TryParse(arguments[2], out var sessionId) || sessionId < 0)
        {
            throw new ArgumentException("Session id must be a non-negative integer.");
        }

        using var response = await client.PostAsJsonAsync(
            $"seats/{Uri.EscapeDataString(arguments[1])}/stream/start",
            new { sessionId, actionToken },
            JsonOptions);
        await EnsureSuccessAsync(response);
        Console.WriteLine(await response.Content.ReadAsStringAsync());
        return 0;
    }

    private static async Task<int> StreamStopAsync(
        HttpClient client,
        IReadOnlyList<string> arguments,
        string? actionToken)
    {
        RequireCount(arguments, 2, "Usage: agent-seat stream-stop <seat-id> --yes");
        RequireConfirmation(arguments);
        RequireActionToken(actionToken);
        using var response = await client.PostAsJsonAsync(
            $"seats/{Uri.EscapeDataString(arguments[1])}/stream/stop",
            new { confirm = true, actionToken },
            JsonOptions);
        await EnsureSuccessAsync(response);
        Console.WriteLine($"Stopped Sunshine for seat '{arguments[1]}'.");
        return 0;
    }

    private static async Task<int> PairAsync(
        HttpClient client,
        List<string> arguments,
        string? actionToken)
    {
        RequireCount(arguments, 3, "Usage: agent-seat pair <seat-id> <four-digit-pin> [--name NAME]");
        RequireActionToken(actionToken);
        var name = TakeOption(arguments, "--name") ?? "Moonlight client";
        var pin = arguments[2];
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Moonlight PIN must contain exactly four digits.");
        }

        using var response = await client.PostAsJsonAsync(
            $"seats/{Uri.EscapeDataString(arguments[1])}/stream/pair",
            new { pin, clientName = name, actionToken },
            JsonOptions);
        await EnsureSuccessAsync(response);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Console.WriteLine(result.GetProperty("detail").GetString());
        return 0;
    }

    private static async Task<int> SessionActionAsync(
        HttpClient client,
        IReadOnlyList<string> arguments,
        string action,
        string? actionToken)
    {
        RequireCount(arguments, 3, $"Usage: agent-seat {action} <seat-id> <session-id> --yes");
        RequireConfirmation(arguments);
        RequireActionToken(actionToken);
        if (!int.TryParse(arguments[2], out var sessionId) || sessionId < 0)
        {
            throw new ArgumentException("Session id must be a non-negative integer.");
        }

        using var response = await client.PostAsJsonAsync(
            $"seats/{Uri.EscapeDataString(arguments[1])}/{action}",
            new { sessionId, confirm = true, actionToken },
            JsonOptions);
        await EnsureSuccessAsync(response);
        Console.WriteLine($"Requested {action} for session {sessionId}.");
        return 0;
    }

    private static async Task<int> PrintJsonAsync(HttpClient client, string path)
    {
        using var document = await GetJsonAsync(client, path);
        Console.WriteLine(JsonSerializer.Serialize(document.RootElement, JsonOptions));
        return 0;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await EnsureSuccessAsync(response);
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                body = error.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Keep the raw response body.
        }

        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    private static string? TakeOption(List<string> arguments, string name)
    {
        var index = arguments.FindIndex(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= arguments.Count)
        {
            throw new ArgumentException($"Option {name} requires a value.");
        }

        var value = arguments[index + 1];
        arguments.RemoveRange(index, 2);
        return value;
    }

    private static bool TakeFlag(List<string> arguments, string name)
    {
        var index = arguments.FindIndex(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        arguments.RemoveAt(index);
        return true;
    }

    private static int ParseIntOption(List<string> arguments, string name, int fallback)
    {
        var value = TakeOption(arguments, name);
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Option {name} requires an integer.");
    }

    private static void RequireConfirmation(IReadOnlyList<string> arguments)
    {
        if (!arguments.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("This action changes state. Add --yes after reviewing the target.");
        }
    }

    private static void RequireActionToken(string? actionToken)
    {
        if (string.IsNullOrWhiteSpace(actionToken))
        {
            throw new ArgumentException(
                "This action requires AGENTSEAT_ACTION_TOKEN or the --action-token option.");
        }
    }

    private static void RequireCount(IReadOnlyCollection<string> arguments, int count, string usage)
    {
        if (arguments.Count < count)
        {
            throw new ArgumentException(usage);
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'agent-seat help'.");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            AgentSeat CLI

              agent-seat status [--url URL]
              agent-seat list
              agent-seat sessions
              agent-seat preflight
              agent-seat add <id> <windows-user> [--name NAME] [--host HOST]
                             [--port PORT] [--sunshine-port PORT] [--width PX] [--height PX]
                             [--windowed] [--clipboard] [--agent]
              agent-seat agent-control <seat-id> <on|off>
              agent-seat computer <command>      drive an agent seat's background desktop
                                                 (run 'agent-seat computer guide')
              agent-seat rdp <id> [output-file]
              agent-seat stream-status <seat-id>
              agent-seat stream-start <seat-id> <session-id> --yes
              agent-seat stream-stop <seat-id> --yes
              agent-seat pair <seat-id> <four-digit-pin> [--name NAME]
              agent-seat remove <id> --yes
              agent-seat disconnect <seat-id> <session-id> --yes
              agent-seat logoff <seat-id> <session-id> --yes

            The default service URL is http://127.0.0.1:38399.
            Streaming and session actions require AGENTSEAT_ACTION_TOKEN or --action-token TOKEN.
            """);
    }
}
