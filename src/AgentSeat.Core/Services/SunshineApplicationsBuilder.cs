using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentSeat.Core.Services;

/// <summary>
/// Builds the Steam-only Sunshine application catalog used by a new seat.
/// Steam authentication remains entirely owned by Steam in the seat user's Windows profile.
/// </summary>
public static class SunshineApplicationsBuilder
{
    private const string SteamName = "Steam";
    private const string DesktopName = "Desktop";
    private const string LegacySteamCommand = "steam://open/bigpicture";

    public static string BuildDefaultCatalog(
        string seatId,
        string steamExecutablePath,
        string steamLauncherPath,
        string compatibilityDirectory)
    {
        if (!Regex.IsMatch(seatId, "^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Seat ID is not safe for a Steam IPC name.", nameof(seatId));
        }

        ValidateCommandPath(steamExecutablePath, nameof(steamExecutablePath));
        ValidateCommandPath(steamLauncherPath, nameof(steamLauncherPath));
        ValidateCommandPath(compatibilityDirectory, nameof(compatibilityDirectory));

        var steamCommand =
            $"\"{steamLauncherPath}\" --seat {seatId} --source \"{steamExecutablePath}\" --compat \"{compatibilityDirectory}\"";
        var document = new JsonObject
        {
            ["env"] = new JsonObject(),
            ["apps"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = SteamName,
                    ["cmd"] = steamCommand,
                    ["auto-detach"] = true,
                    ["wait-all"] = true,
                    ["image-path"] = "steam.png"
                }
            }
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
               Environment.NewLine;
    }

    /// <summary>
    /// Recognizes only the untouched Desktop + Steam catalog shipped by upstream Sunshine.
    /// Any user-added or edited application makes this return false so provisioning never
    /// overwrites a customized catalog.
    /// </summary>
    public static bool IsUnmodifiedUpstreamDefault(string content)
    {
        try
        {
            var root = JsonNode.Parse(content) as JsonObject;
            if (root is null || root.Count != 2 ||
                root["env"] is not JsonObject { Count: 0 } ||
                root["apps"] is not JsonArray { Count: 2 } apps)
            {
                return false;
            }

            var desktop = apps.OfType<JsonObject>()
                .SingleOrDefault(app => StringValue(app, "name") == "Desktop");
            var steam = apps.OfType<JsonObject>()
                .SingleOrDefault(app => StringValue(app, "name") == "Steam Big Picture");

            return IsDesktopDefault(desktop) && IsSteamDefault(steam);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool IsUnmodifiedAgentSeatDefault(string content, string seatId)
    {
        try
        {
            var root = JsonNode.Parse(content) as JsonObject;
            if (root is null || root.Count != 2 ||
                root["env"] is not JsonObject { Count: 0 } ||
                root["apps"] is not JsonArray apps ||
                apps.Count is not (1 or 2) ||
                apps.OfType<JsonObject>().SingleOrDefault(app => StringValue(app, "name") == SteamName) is not
                    JsonObject { Count: 5 } steam ||
                StringValue(steam, "name") != SteamName ||
                StringValue(steam, "image-path") != "steam.png" ||
                BooleanValue(steam, "auto-detach") != true ||
                BooleanValue(steam, "wait-all") != true)
            {
                return false;
            }

            if (apps.Count == 2 &&
                !IsDesktopDefault(apps.OfType<JsonObject>()
                    .SingleOrDefault(app => StringValue(app, "name") == DesktopName)))
            {
                return false;
            }

            var command = StringValue(steam, "cmd");
            if (command == LegacySteamCommand)
            {
                return true;
            }

            var directLaunchSuffix =
                $" -master_ipc_name_override AgentSeat_{seatId} -bigpicture";
            if (command is not null &&
                command.StartsWith('\"') &&
                command.EndsWith(directLaunchSuffix, StringComparison.Ordinal) &&
                command[..^directLaunchSuffix.Length].EndsWith('\"'))
            {
                return true;
            }

            var escapedSeatId = Regex.Escape(seatId);
            return command is not null && Regex.IsMatch(
                command,
                $"^\"[^\"\\r\\n]+\" --seat {escapedSeatId} --source \"[^\"\\r\\n]+\" --compat \"[^\"\\r\\n]+\"$",
                RegexOptions.CultureInvariant);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsDesktopDefault(JsonObject? app) =>
        app is { Count: 2 } &&
        StringValue(app, "name") == DesktopName &&
        StringValue(app, "image-path") == "desktop.png";

    private static bool IsSteamDefault(JsonObject? app)
    {
        if (app is not { Count: 6 } ||
            StringValue(app, "name") != "Steam Big Picture" ||
            StringValue(app, "cmd") != LegacySteamCommand ||
            StringValue(app, "image-path") != "steam.png" ||
            BooleanValue(app, "auto-detach") != true ||
            BooleanValue(app, "wait-all") != true ||
            app["prep-cmd"] is not JsonArray { Count: 1 } prepCommands ||
            prepCommands[0] is not JsonObject { Count: 2 } prepCommand)
        {
            return false;
        }

        return StringValue(prepCommand, "do") == string.Empty &&
               StringValue(prepCommand, "undo") == "steam://close/bigpicture";
    }

    private static string? StringValue(JsonObject value, string key) =>
        value[key]?.GetValue<string>();

    private static bool? BooleanValue(JsonObject value, string key) =>
        value[key]?.GetValue<bool>();

    private static void ValidateCommandPath(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\"', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("Command path is invalid.", parameterName);
        }
    }
}
