using System.Net;
using System.Text.Json.Nodes;
using AgentSeat.Core.Agent;

namespace AgentSeat.Core.Tests;

public sealed class ComputerObservationTests
{
    // A valid tiny PNG; dimensions below describe a simulated 1280x800 desktop, not a token estimate.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aJ1kAAAAASUVORK5CYII=";

    private sealed class Handler(Func<JsonObject, JsonObject> responder) : HttpMessageHandler
    {
        internal List<JsonObject> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? new JsonObject() : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            Requests.Add((JsonObject)body.DeepClone());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responder(body).ToJsonString()) };
        }
    }

    private static JsonObject Batch(JsonObject request, bool ui = true, long handle = 10, string text = "[1] Edit value=hello", bool unchanged = false) => new()
    {
        ["ok"] = true,
        ["results"] = new JsonArray((request["actions"] as JsonArray ?? []).OfType<JsonObject>().Select(action => (JsonNode?)new JsonObject
        {
            ["action"] = action["type"]!.DeepClone(), ["ok"] = true,
            ["data"] = action["type"]!.GetValue<string>() == "observe"
                ? new JsonObject { ["available"] = ui, ["windowHandle"] = handle, ["text"] = text }
                : null
        }).ToArray()),
        ["screenshot"] = request["screenshot"]?.GetValue<bool>() == true ? new JsonObject
        {
            ["mimeType"] = "image/png", ["dataBase64"] = Png, ["width"] = 1280, ["height"] = 800,
            ["signature"] = "frame", ["changed"] = !unchanged
        } : null
    };

    private static HttpClient Client(Handler handler) => new(handler) { BaseAddress = new Uri("http://127.0.0.1:38399/api/v1/") };
    private static int Images(JsonObject result) => result["content"]!.AsArray().Count(item => item?["type"]?.GetValue<string>() == "image");
    private static JsonObject Metadata(JsonObject result) => JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    [Fact]
    public async Task NewConversationAlwaysGetsAFullImageEvenWhenTextWasRequested()
    {
        var handler = new Handler(body => Batch(body));
        using var client = Client(handler);
        var session = new ComputerObservationSession(client, "agent");
        var result = await session.ObserveAsync(new JsonObject { ["image"] = "never", ["region"] = new JsonArray(1, 2, 30, 40) }, null, default);
        Assert.Equal(1, Images(result));
        Assert.True(Metadata(result)["firstObservation"]!.GetValue<bool>());
        Assert.Null(Assert.Single(handler.Requests)["region"]);
        Assert.Null(handler.Requests[0]["since"]);
        var secondConnection = new ComputerObservationSession(client, "agent");
        Assert.Equal(1, Images(await secondConnection.ObserveAsync(new JsonObject(), null, default)));
    }

    [Fact]
    public async Task InputRequiresAnInitialImageAndMakesNoHttpCallWhenMissing()
    {
        var handler = new Handler(body => Batch(body));
        using var client = Client(handler);
        var session = new ComputerObservationSession(client, "agent");
        var result = await session.ObserveAsync(new JsonObject(), new JsonArray(new JsonObject { ["type"] = "click", ["x"] = 1, ["y"] = 2 }), default);
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TenTextObservationsDeliverOneImageAndSuppressRepeatedUiText()
    {
        var handler = new Handler(body => Batch(body));
        using var client = Client(handler);
        var session = new ComputerObservationSession(client, "agent");
        var images = 0;
        for (var index = 0; index < 10; index++)
        {
            var result = await session.ObserveAsync(new JsonObject(), null, default);
            images += Images(result);
            if (index > 0) Assert.Null(Metadata(result)["ui"]);
        }
        Assert.Equal(1, images);
        Assert.Equal(10, handler.Requests.Count);
        Assert.All(handler.Requests.Skip(1), request => Assert.False(request["screenshot"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task MissingAccessibilityUsesImageDiffAndNeverReplaysInput()
    {
        var handler = new Handler(body => Batch(body, ui: false, unchanged: body["since"] is not null));
        using var client = Client(handler);
        var session = new ComputerObservationSession(client, "agent");
        await session.ObserveAsync(new JsonObject(), null, default);
        var result = await session.ObserveAsync(new JsonObject(), new JsonArray(new JsonObject { ["type"] = "type", ["text"] = "hello" }), default);
        Assert.Equal(0, Images(result));
        Assert.Equal(1, handler.Requests.Sum(body => body["actions"]!.AsArray().Count(action => action?["type"]?.GetValue<string>() == "type")));
        Assert.Equal("frame", handler.Requests.Last()["since"]!.GetValue<string>());
        Assert.Empty(handler.Requests.Last()["actions"]!.AsArray());
    }

    [Fact]
    public async Task SwitchingWindowsAndExplicitVisualVerificationReturnFullImages()
    {
        long handle = 10;
        var handler = new Handler(body => Batch(body, handle: handle));
        using var client = Client(handler);
        var session = new ComputerObservationSession(client, "agent");
        await session.ObserveAsync(new JsonObject(), null, default);
        handle = 11;
        Assert.Equal(1, Images(await session.ObserveAsync(new JsonObject(), null, default)));
        Assert.Null(handler.Requests.Last()["since"]);
        Assert.Equal(1, Images(await session.ObserveAsync(new JsonObject { ["image"] = "always" }, null, default)));
    }

    [Fact]
    public async Task ChangedCropsCarryScreenOffsetsAndDontIncludeTheFullImage()
    {
        var handler = new Handler(body =>
        {
            var result = Batch(body, ui: false);
            if (body["since"] is not null)
                result["screenshot"]!["changes"] = new JsonArray(new JsonObject
                {
                    ["x"] = 100, ["y"] = 200, ["width"] = 32, ["height"] = 32, ["mimeType"] = "image/png", ["dataBase64"] = Png
                });
            return result;
        });
        using var client = Client(handler);
        var session = new ComputerObservationSession(client, "agent");
        await session.ObserveAsync(new JsonObject(), null, default);
        var result = await session.ObserveAsync(new JsonObject(), null, default);
        Assert.Equal(1, Images(result));
        Assert.Equal(100, Metadata(result)["imageRegions"]![0]!["x"]!.GetValue<int>());
        Assert.Null(Metadata(result)["imageSize"]);
    }

    [Fact]
    public async Task OwnerPausePreventsFallbackScreenshot()
    {
        var handler = new Handler(_ => new JsonObject { ["ok"] = false, ["errorCode"] = "paused" });
        using var client = Client(handler);
        var session = new ComputerObservationSession(client, "agent");
        var result = await session.ObserveAsync(new JsonObject(), null, default);
        Assert.Single(handler.Requests);
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal(0, Images(result));
    }

    [Fact]
    public async Task ResetRequiresAnotherInitialImageBeforeInput()
    {
        var handler = new Handler(body => Batch(body));
        using var client = Client(handler);
        var session = new ComputerObservationSession(client, "agent");
        await session.ObserveAsync(new JsonObject(), null, default);
        session.Reset();
        Assert.True((await session.ObserveAsync(new JsonObject(), new JsonArray(), default))["isError"]!.GetValue<bool>());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void ObservationBoundsAreValidatedBeforeReachingTheHelper()
    {
        Assert.Null(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Observe }));
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Observe, MaxElements = 151 }));
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Observe, MaxCharacters = 12001 }));
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Observe, Handle = -1 }));
        Assert.True(AgentActionBatch.TryParse(JsonNode.Parse("""[{"type":"observe","maxElements":12}]"""), out var batch, out _));
        Assert.Equal(12, Assert.Single(batch!.Steps).Request.MaxElements);
    }

    [Fact]
    public async Task McpNegotiationAndDiscoveryDontNeedTheServiceOrLeakCredentials()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Service must not be called."));
        using var client = Client(handler);
        var server = new ComputerMcp(client, "agent", "secret-token");
        var initialized = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""")!.AsObject());
        Assert.Equal("2025-11-25", initialized!["result"]!["protocolVersion"]!.GetValue<string>());
        await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")!.AsObject());
        var list = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")!.AsObject());
        Assert.Equal(4, list!["result"]!["tools"]!.AsArray().Count);
        Assert.DoesNotContain("secret-token", initialized.ToJsonString() + list.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":42,\"id\":1,\"method\":\"ping\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":{},\"method\":\"ping\"}")]
    public async Task MalformedRpcReturnsAProtocolError(string json)
    {
        using var client = Client(new Handler(body => Batch(body)));
        var result = await new ComputerMcp(client, "agent", null).HandleAsync(JsonNode.Parse(json)!.AsObject());
        Assert.Equal(-32600, result!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task StdioMessagesAreNewlineDelimitedAndNotificationsHaveNoResponse()
    {
        using var client = Client(new Handler(body => Batch(body)));
        using var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}
            """);
        using var output = new StringWriter();
        Assert.Equal(0, await ComputerMcp.RunAsync(client, "agent", null, input, output));
        var messages = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(new[] { 1, 2 }, messages.Select(message => message["id"]!.GetValue<int>()).Order().ToArray());
        Assert.All(messages, message => Assert.Equal("2.0", message["jsonrpc"]!.GetValue<string>()));
    }

    [Fact]
    public async Task UnicodeInToolTextIsReadableInsteadOfDoubleEscaped()
    {
        using var client = Client(new Handler(body => Batch(body, text: "[1] Edit value=안녕하세요")));
        var result = await new ComputerObservationSession(client, "agent").ObserveAsync(new JsonObject(), null, default);
        Assert.Contains("안녕하세요", result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("\\uC548", result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdioDecodesUtf8KoreanIndependentOfWindowsConsoleEncoding()
    {
        using var client = Client(new Handler(body => Batch(body)));
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            {"jsonrpc":"2.0","id":"한글 검증","method":"initialize"}
            """));
        using var output = new MemoryStream();
        Assert.Equal(0, await ComputerMcp.RunStdioAsync(client, "agent", null, input, output));
        var bytes = output.ToArray();
        Assert.Equal((byte)'{', bytes[0]); // No BOM on the MCP output pipe.
        Assert.Equal("한글 검증", JsonNode.Parse(bytes)!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[]")]
    public async Task MalformedCancellationNotificationDoesNotTerminateTheServer(string parameters)
    {
        using var client = Client(new Handler(body => Batch(body)));
        var server = new ComputerMcp(client, "agent", null);
        Assert.Null(await server.HandleAsync(JsonNode.Parse(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":" + parameters + "}")!.AsObject()));
        var ping = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping"}""")!.AsObject());
        Assert.NotNull(ping!["result"]);
    }

    private sealed class CancelHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            if (Calls > 1)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Batch(body).ToJsonString()) };
        }
    }

    [Fact]
    public async Task CancellationRejectsMoreInputUntilTheCurrentScreenIsObservedAgain()
    {
        var handler = new CancelHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:38399/api/v1/") };
        var server = new ComputerMcp(client, "agent", new string('t', 32));
        await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""")!.AsObject());
        await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")!.AsObject());
        await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"seat_observe"}}""")!.AsObject());
        var pending = server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"seat_act","arguments":{"actions":[{"type":"type","text":"hello"}]}}}""")!.AsObject());
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}""")!.AsObject());
        var cancelled = await pending;
        Assert.True(cancelled!["result"]!["isError"]!.GetValue<bool>());
        var retry = await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"seat_act","arguments":{"actions":[]}}}""")!.AsObject());
        Assert.Equal("observation_required", Metadata(retry!["result"]!.AsObject())["errorCode"]!.GetValue<string>());
        Assert.Equal(2, handler.Calls);
    }
}
