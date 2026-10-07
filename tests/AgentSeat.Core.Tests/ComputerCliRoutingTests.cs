using System.Net;
using System.Text;

namespace AgentSeat.Core.Tests;

/// <summary>
/// Pins which API each <c>agent-seat computer</c> command calls. A command added later once shadowed an existing one:
/// the new "close the seat" took over "close this window", so an AI that wanted to close one window ended the
/// whole seat session. The CLI has no other tests, so this is where that class of mistake gets caught.
/// </summary>
public sealed class ComputerCliRoutingTests
{
    private sealed record Call(HttpMethod Method, string Path, string Body);

    private sealed class RecordingHandler(string response, string? seatsJson = null) : HttpMessageHandler
    {
        internal List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(request.Method, request.RequestUri!.AbsolutePath, body));
            var content = seatsJson is not null && request.RequestUri.AbsolutePath.EndsWith("/seats", StringComparison.Ordinal)
                ? seatsJson
                : response;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }

    private static string Seats(params (string Id, bool Agent, bool Streaming)[] seats) =>
        "[" + string.Join(',', seats.Select(seat =>
            $$"""{ "seat": { "id": "{{seat.Id}}", "agentControlEnabled": {{(seat.Agent ? "true" : "false")}}, "streamingEnabled": {{(seat.Streaming ? "true" : "false")}} } }""")) + "]";

    /// <summary>Runs without --seat, so the CLI has to pick the seat itself from GET /seats.</summary>
    private static async Task<List<Call>> RunDiscoveringAsync(string seatsJson, params string[] commandAndArguments)
    {
        var handler = new RecordingHandler(EmptyResult, seatsJson);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:38399/api/v1/") };
        var arguments = new List<string> { "computer", "--agent-token", new string('t', 32) };
        arguments.AddRange(commandAndArguments);
        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            _ = await ComputerCli.RunAsync(client, arguments, actionToken: null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return handler.Calls;
    }

    private sealed record Run(List<Call> Calls, string Output, int ExitCode);

    private const string EmptyResult = """{ "ok": true, "completed": 0, "results": [] }""";

    private static Task<Call> RunAsync(params string[] commandAndArguments) => RunForSeatAsync("agent", commandAndArguments);

    private static async Task<Call> RunForSeatAsync(string seat, params string[] commandAndArguments) =>
        Assert.Single((await RunCapturingAsync(seat, EmptyResult, commandAndArguments)).Calls);

    private static async Task<Run> RunCapturingAsync(string seat, string response, params string[] commandAndArguments)
    {
        var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:38399/api/v1/") };
        var arguments = new List<string> { "computer", "--seat", seat, "--agent-token", new string('t', 32) };
        arguments.AddRange(commandAndArguments);

        var originalOut = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        int exitCode;
        try
        {
            exitCode = await ComputerCli.RunAsync(client, arguments, actionToken: null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return new Run(handler.Calls, output.ToString(), exitCode);
    }

    [Fact]
    public async Task CloseWithAHandleClosesOneWindowAndNeverTheSeat()
    {
        var call = await RunAsync("close", "131472");

        Assert.Equal("/api/v1/seats/agent/agent/actions", call.Path);
        Assert.Contains("close_window", call.Body, StringComparison.Ordinal);
        Assert.Contains("131472", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloseSeatIsTheOnlyCommandThatEndsTheSession()
    {
        var call = await RunAsync("close-seat");

        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/v1/seats/agent/agent/close", call.Path);
        Assert.Contains("\"keepFiles\":false", NoWhitespace(call.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepFilesIsPassedOnlyWhenAsked()
    {
        var call = await RunAsync("close-seat", "--keep-files");

        Assert.Contains("\"keepFiles\":true", NoWhitespace(call.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FocusStillTargetsAWindow()
    {
        var call = await RunAsync("focus", "42");

        Assert.Equal("/api/v1/seats/agent/agent/actions", call.Path);
        Assert.Contains("focus_window", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesCleanAsksTheServiceToWipeTheShare()
    {
        var call = await RunAsync("files", "clean");

        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/v1/seats/agent/agent/files/clean", call.Path);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("stop")]
    public async Task OwnerControlsUseTheManagementApiNotTheAgentApi(string command)
    {
        // A seat id that cannot be mistaken for a path segment ("agent" would match "/agent/").
        var call = await RunForSeatAsync("s1", command);

        Assert.Equal($"/api/v1/seats/s1/agent-control/{command}", call.Path);
        Assert.DoesNotContain("/s1/agent/", call.Path, StringComparison.Ordinal);
    }

    private static string NoWhitespace(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    [Theory]
    [InlineData("key-down shift", "\"type\":\"key_down\",\"keys\":\"shift\"")]
    [InlineData("key-up", "\"type\":\"key_up\"}")]
    [InlineData("key-up shift w", "\"type\":\"key_up\",\"keys\":\"shiftw\"")]
    [InlineData("mouse-down 10 20 --right", "\"type\":\"mouse_down\",\"button\":\"right\",\"x\":10,\"y\":20")]
    [InlineData("mouse-up", "\"type\":\"mouse_up\",\"button\":\"left\"}")]
    [InlineData("hold w 2000", "\"type\":\"hold\",\"keys\":\"w\",\"ms\":2000")]
    [InlineData("long-press 5 6 800 --middle", "\"type\":\"long_press\",\"button\":\"middle\",\"x\":5,\"y\":6,\"ms\":800")]
    [InlineData("release", "\"type\":\"release\"}")]
    [InlineData("zoom 1 2 30 40", "\"type\":\"zoom\",\"x\":1,\"y\":2,\"width\":30,\"height\":40")]
    [InlineData("key Down --repeat 5", "\"type\":\"key\",\"keys\":\"Down\",\"repeat\":5")]
    public async Task HeldInputZoomAndRepeatCommandsAreAgentActions(string command, string expected)
    {
        var call = await RunAsync(command.Split(' '));

        Assert.Equal("/api/v1/seats/agent/agent/actions", call.Path);
        Assert.Contains(expected, NoWhitespace(call.Body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mouse-down 10")]
    [InlineData("long-press 5 6")]
    [InlineData("hold 2000")]
    [InlineData("zoom 1 2 3")]
    [InlineData("key-down")]
    public async Task MalformedHeldInputCommandsSendNothing(string command)
    {
        var run = await RunCapturingAsync("agent", EmptyResult, command.Split(' '));

        Assert.Empty(run.Calls);
        Assert.Equal(2, run.ExitCode);
        Assert.Contains("invalid_arguments", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActionsAskForAChangeReportAndASettledScreenByDefault()
    {
        var call = NoWhitespace((await RunAsync("click", "1", "2")).Body);
        var noDiff = NoWhitespace((await RunAsync("click", "1", "2", "--no-diff", "--stable", "0")).Body);

        Assert.Contains("\"diff\":true", call, StringComparison.Ordinal);
        Assert.Contains("\"stableMs\":1000", call, StringComparison.Ordinal);
        Assert.Contains("\"diff\":false", noDiff, StringComparison.Ordinal);
        Assert.Contains("\"stableMs\":0", noDiff, StringComparison.Ordinal);
        Assert.DoesNotContain("\"since\"", noDiff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputIsCompactAndTheNextCommandAsksWhatChangedSinceThisScreenshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agent-seat-cli-test-{Guid.NewGuid():N}");
        const string signature = "t32:0,0,32,32:AAAAAA==";
        var response = $$"""
            {
              "ok": true, "completed": 3,
              "results": [
                { "index": 0, "action": "click", "ok": true, "data": { "cursorX": 1, "cursorY": 2 } },
                { "index": 1, "action": "key_down", "ok": true, "data": { "held": ["shift"] } },
                { "index": 2, "action": "windows", "ok": true, "data": { "windows": [] } }
              ],
              "screenshot": {
                "mimeType": "image/png", "width": 32, "height": 32, "dataBase64": "AAAA",
                "signature": "{{signature}}", "changed": true,
                "changes": [ { "x": 0, "y": 0, "width": 32, "height": 32, "mimeType": "image/png", "dataBase64": "AAAA" } ]
              }
            }
            """;
        try
        {
            var first = await RunCapturingAsync("agent", response, "--out-dir", directory, "--context", "conversation-a", "click", "1", "2");

            Assert.Equal(0, first.ExitCode);
            var output = first.Output.Trim();
            Assert.DoesNotContain('\n', output);
            Assert.Contains("\"held\":[\"shift\"]", output, StringComparison.Ordinal);
            Assert.Contains("\"action\":\"windows\"", output, StringComparison.Ordinal);
            Assert.DoesNotContain("cursorX", output, StringComparison.Ordinal);
            Assert.DoesNotContain("\"action\":\"click\"", output, StringComparison.Ordinal);
            Assert.DoesNotContain("dataBase64", output, StringComparison.Ordinal);
            Assert.DoesNotContain("signature", output, StringComparison.Ordinal);
            Assert.DoesNotContain("mimeType", output, StringComparison.Ordinal);
            Assert.Contains("\"changed\":true", output, StringComparison.Ordinal);
            Assert.Contains("-change1.png", output, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "contexts", "conversation-a"), "screen-*-change1.png"));

            var second = await RunCapturingAsync("agent", EmptyResult, "--out-dir", directory, "--context", "conversation-a", "click", "3", "4");
            Assert.Contains($"\"since\":\"{signature}\"", NoWhitespace(Assert.Single(second.Calls).Body), StringComparison.Ordinal);

            var otherConversation = await RunCapturingAsync("agent", EmptyResult, "--out-dir", directory, "--context", "conversation-b", "click", "3", "4");
            Assert.DoesNotContain("\"since\"", Assert.Single(otherConversation.Calls).Body, StringComparison.Ordinal);
            var noContext = await RunCapturingAsync("agent", EmptyResult, "--out-dir", directory, "click", "3", "4");
            Assert.DoesNotContain("\"since\"", Assert.Single(noContext.Calls).Body, StringComparison.Ordinal);
            var verbose = await RunCapturingAsync("agent", response, "--out-dir", directory, "--context", "conversation-a", "--verbose", "click", "1", "2");
            Assert.Contains('\n', verbose.Output.Trim());
            Assert.Contains("cursorX", verbose.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // Seat ids here are never "agent", so a default the developer saved with `computer default-seat agent` on this
    // PC cannot change the outcome.
    [Fact]
    public async Task WithSeveralAgentSeatsTheOneThatDoesNotStreamIsPicked()
    {
        var calls = await RunDiscoveringAsync(
            Seats(("gamer", true, true), ("robot", true, false), ("other", false, false)),
            "click", "1", "2");

        Assert.Equal("/api/v1/seats/robot/agent/actions", calls.Last().Path);
    }

    [Fact]
    public async Task ASingleAgentSeatIsPickedEvenWhenItStreams()
    {
        var calls = await RunDiscoveringAsync(Seats(("gamer", true, true), ("other", false, false)), "status");

        Assert.Equal("/api/v1/seats/gamer/agent/status", calls.Last().Path);
    }

    [Fact]
    public async Task AmbiguousSeatsAreRefusedInsteadOfGuessed()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            RunDiscoveringAsync(Seats(("gamer", true, true), ("robot", true, true)), "click", "1", "2"));

        Assert.Contains("default-seat", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActionOutputNamesTheSeatFirst()
    {
        var run = await RunCapturingAsync("robot", EmptyResult, "click", "1", "2");

        Assert.StartsWith("{\"seat\":\"robot\",", run.Output.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryAgentCallCarriesTheBearerToken()
    {
        var handler = new RecordingHandler(EmptyResult);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:38399/api/v1/") };
        var originalOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            _ = await ComputerCli.RunAsync(
                client,
                ["computer", "--seat", "agent", "--agent-token", "secret-token-secret-token-12345", "status"],
                actionToken: null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal("secret-token-secret-token-12345", client.DefaultRequestHeaders.Authorization?.Parameter);
    }
}
