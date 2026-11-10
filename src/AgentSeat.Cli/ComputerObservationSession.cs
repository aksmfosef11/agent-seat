using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSeat.Core.Agent;

/// <summary>The observation baseline belongs to this MCP connection, never a file shared with another AI.</summary>
internal sealed class ComputerObservationSession(HttpClient client, string seat)
{
    private static readonly JsonSerializerOptions TextJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _prefix = $"seats/{Uri.EscapeDataString(seat)}/agent";
    private bool _hasInitialImage;
    private string? _signature;
    private string? _uiSignature;
    private long? _window;
    private int _observations;
    private int _images;
    private long _pixels;

    internal void Reset()
    {
        _hasInitialImage = false;
        _signature = null;
        _uiSignature = null;
        _window = null;
    }

    internal async Task<JsonObject> StatusAsync(CancellationToken cancellationToken)
    {
        var status = await RequestAsync("status", null, cancellationToken);
        status["observationUsage"] = new JsonObject
        {
            ["observations"] = _observations, ["imagesReturned"] = _images, ["imagePixelsReturned"] = _pixels
        };
        return TextResult(status);
    }

    internal async Task<JsonObject> StartAsync(CancellationToken cancellationToken)
    {
        var status = await RequestAsync("start", new JsonObject(), cancellationToken);
        Reset();
        return TextResult(status);
    }

    internal async Task<JsonObject> ObserveAsync(JsonObject arguments, JsonArray? actions, CancellationToken cancellationToken)
    {
        var mode = arguments["image"]?.GetValue<string>() ?? "auto";
        if (mode is not ("auto" or "always" or "never")) throw new ArgumentException("image must be auto, always or never.");
        if (actions is not null && !_hasInitialImage)
            return Error("observation_required", "Call seat_observe first to see this conversation's initial full screen. No actions ran.",
                AgentDisposition.NotDelivered);
        if (actions is { Count: > 49 }) throw new ArgumentException("At most 49 actions per call (one slot is reserved for observation).");
        var maxElements = arguments["maxElements"]?.GetValue<int>() ?? 50;
        var maxCharacters = arguments["maxCharacters"]?.GetValue<int>() ?? 3000;
        if (maxElements is < 1 or > 150 || maxCharacters is < 256 or > 12000)
            throw new ArgumentException("maxElements must be 1-150 and maxCharacters 256-12000.");
        var settle = arguments["settleMs"]?.GetValue<int>() ?? (actions is null ? 0 : 250);
        if (settle is < 0 or > 5000) throw new ArgumentException("settleMs must be 0-5000.");
        JsonArray? region = arguments["region"] as JsonArray;
        if (arguments.ContainsKey("region") && (region is null || region.Count != 4))
            throw new ArgumentException("region must be [x,y,width,height].");
        if (region is not null && (region[2]!.GetValue<int>() < 1 || region[3]!.GetValue<int>() < 1))
            throw new ArgumentException("region width and height must be positive.");
        var first = !_hasInitialImage;
        var steps = actions is null ? new JsonArray() : (JsonArray)actions.DeepClone();
        var observe = new JsonObject
        {
            ["type"] = "observe", ["maxElements"] = maxElements, ["maxCharacters"] = maxCharacters
        };
        foreach (var name in new[] { "handle", "rootRef", "interactiveOnly", "maxDepth" })
            if (arguments[name] is { } value) observe[name] = value.DeepClone();
        var scoped = observe.ContainsKey("handle") || observe.ContainsKey("rootRef");
        var observationIndex = steps.Count;
        steps.Add(observe);
        var body = new JsonObject
        {
            ["actions"] = steps, ["screenshot"] = first || mode == "always" || region is not null,
            ["settleMs"] = settle, ["stableMs"] = 500, ["diff"] = true
        };
        if (!first && region is not null) body["region"] = Region(region);
        var batch = await RequestAsync("actions", body, cancellationToken);
        var entries = batch["results"] as JsonArray;
        var observationEntry = entries?.OfType<JsonObject>().LastOrDefault(entry =>
            entry["action"]?.GetValue<string>() == "observe" && entry["index"]?.GetValue<int>() == observationIndex);
        var observation = observationEntry?["data"] as JsonObject;
        var window = observation?["windowHandle"]?.GetValue<long>();
        var switchedWindow = _window is not null && window is not null && _window != window;
        var image = batch["screenshot"] as JsonObject;
        // UIA is optional. Old helpers, games, canvas apps and hung providers fall back to images. Never retry input.
        var halted = batch["errorCode"]?.GetValue<string>() is "paused" or "stopped" or "disabled";
        if (image is null && !halted && (first || mode == "always" ||
            (mode == "auto" && !scoped && (observation?["available"]?.GetValue<bool>() != true || switchedWindow ||
                                batch["ok"]?.GetValue<bool>() == false))))
        {
            var screenshotBody = new JsonObject
            {
                ["actions"] = new JsonArray(), ["screenshot"] = true, ["settleMs"] = 0,
                ["stableMs"] = 500, ["diff"] = true
            };
            if (!first && !switchedWindow && mode == "auto" && _signature is not null)
                screenshotBody["since"] = _signature;
            if (!first && region is not null) screenshotBody["region"] = Region(region);
            try
            {
                var capture = await RequestAsync("actions", screenshotBody, cancellationToken);
                image = capture["screenshot"] as JsonObject;
                if (image is null) batch["screenshotError"] = capture["error"]?.DeepClone() ?? JsonValue.Create("No image returned.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                // The action response already arrived. Preserve it even when the follow-up image fails.
                batch["screenshotError"] = "The follow-up image was unavailable.";
            }
        }
        var postObservationFailed = actions is not null && !halted && entries is { Count: > 1 } &&
                                    observationEntry?["ok"]?.GetValue<bool>() == false;
        var metadata = new JsonObject
        {
            ["seat"] = seat, ["ok"] = postObservationFailed || batch["ok"]?.GetValue<bool>() == true
        };
        foreach (var name in new[] { "screenshotError", "disposition" })
            if (batch[name] is { } value) metadata[name] = value.DeepClone();
        if (postObservationFailed)
        {
            metadata["observationError"] = new JsonObject
            {
                ["errorCode"] = batch["errorCode"]?.DeepClone(),
                ["error"] = batch["error"]?.DeepClone()
            };
        }
        else
        {
            foreach (var name in new[] { "errorCode", "error", "failedIndex" })
                if (batch[name] is { } value) metadata[name] = value.DeepClone();
        }
        if (observation is not null)
        {
            var signature = observation.ToJsonString();
            metadata["uiChanged"] = signature != _uiSignature;
            if (signature != _uiSignature || first || scoped) metadata["ui"] = observation.DeepClone();
            _uiSignature = signature;
            _window = window;
        }
        // Retain useful data (window lists, launch PID, held input), not per-action pointer echoes.
        if (actions is not null)
        {
            var useful = new JsonArray();
            var outcomes = new JsonArray();
            var completed = 0;
            foreach (var entry in entries?.OfType<JsonObject>() ?? [])
            {
                if (ReferenceEquals(entry, observationEntry)) continue;
                var outcome = (JsonObject)entry.DeepClone();
                if (outcome["action"]?.GetValue<string>() != AgentActions.WaitFor) outcome.Remove("data");
                if (entry["ok"]?.GetValue<bool>() == true) completed++;
                outcomes.Add(outcome);
                if (entry["data"] is not JsonObject data) continue;
                var copy = (JsonObject)data.DeepClone();
                copy.Remove("cursorX"); copy.Remove("cursorY");
                if (copy.Count > 0) useful.Add(copy);
            }
            metadata["completed"] = completed;
            metadata["actionResults"] = outcomes;
            if (useful.Count > 0) metadata["results"] = useful;
        }
        var content = new JsonArray();
        if (image is not null)
        {
            var crops = image["changes"] as JsonArray;
            var useCrops = !first && !switchedWindow && mode == "auto" && region is null &&
                           crops is { Count: > 0 } && crops.All(crop => crop?["dataBase64"] is not null);
            if (useCrops)
            {
                var offsets = new JsonArray();
                foreach (var crop in crops!.OfType<JsonObject>())
                {
                    if (AddImage(content, crop))
                    {
                        offsets.Add(new JsonObject { ["x"] = crop["x"]?.DeepClone(), ["y"] = crop["y"]?.DeepClone(),
                            ["width"] = crop["width"]?.DeepClone(), ["height"] = crop["height"]?.DeepClone() });
                    }
                }
                metadata["imageRegions"] = offsets;
            }
            else if (first || switchedWindow || mode == "always" || image["changed"]?.GetValue<bool>() != false)
            {
                if (AddImage(content, image))
                {
                    metadata["imageSize"] = new JsonArray(image["width"]?.DeepClone(), image["height"]?.DeepClone());
                    if (region is not null && !first) metadata["imageRegion"] = region.DeepClone();
                    if (first) _hasInitialImage = true;
                }
            }
            else metadata["screenChanged"] = false;
            // Only compare against an image actually delivered to this client (or an unchanged image).
            if (_hasInitialImage && (content.Count > 0 || image["changed"]?.GetValue<bool>() == false))
                _signature = image["signature"]?.GetValue<string>() ?? _signature;
        }
        if (first) metadata["firstObservation"] = true;
        if (!_hasInitialImage && !metadata.ContainsKey("error"))
        {
            metadata["ok"] = false;
            metadata["error"] = "Initial image unavailable. Observe again; do not repeat actions.";
        }
        _observations++;
        content.Insert(0, new JsonObject { ["type"] = "text", ["text"] = metadata.ToJsonString(TextJson) });
        return new JsonObject { ["content"] = content, ["isError"] = metadata["ok"]?.GetValue<bool>() != true };
    }

    private bool AddImage(JsonArray content, JsonObject image)
    {
        var mime = image["mimeType"]?.GetValue<string>();
        var data = image["dataBase64"]?.GetValue<string>();
        if (mime is not ("image/png" or "image/jpeg") || string.IsNullOrEmpty(data)) return false;
        content.Add(new JsonObject { ["type"] = "image", ["mimeType"] = mime, ["data"] = data });
        _images++;
        _pixels += (long)(image["width"]?.GetValue<int>() ?? 0) * (image["height"]?.GetValue<int>() ?? 0);
        return true;
    }

    private async Task<JsonObject> RequestAsync(string route, JsonObject? body, CancellationToken cancellationToken)
    {
        using var response = body is null ? await client.GetAsync($"{_prefix}/{route}", cancellationToken)
            : await client.PostAsJsonAsync($"{_prefix}/{route}", body, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = JsonNode.Parse(text) as JsonObject ?? throw new HttpRequestException("Service returned non-object JSON.");
        if (!response.IsSuccessStatusCode) parsed["ok"] = false;
        return parsed;
    }

    private static JsonObject Region(JsonArray region) => new()
    {
        ["x"] = region[0]!.GetValue<int>(), ["y"] = region[1]!.GetValue<int>(),
        ["width"] = region[2]!.GetValue<int>(), ["height"] = region[3]!.GetValue<int>()
    };

    internal static JsonObject TextResult(JsonObject value) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value.ToJsonString(TextJson) }),
        ["isError"] = value["ok"]?.GetValue<bool>() == false
    };

    internal static JsonObject Error(string code, string message, AgentDisposition? disposition = null)
    {
        var error = new JsonObject { ["ok"] = false, ["errorCode"] = code, ["error"] = message };
        if (disposition is not null) error["disposition"] = JsonSerializer.SerializeToNode(disposition, AgentJson.Options);
        return TextResult(error);
    }
}
