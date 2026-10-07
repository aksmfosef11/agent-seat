using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentSeat.Core.Agent;

/// <summary>One executable step; <see cref="SourceIndex"/> is the position of the action it came from.</summary>
public sealed record AgentBatchStep(int SourceIndex, AgentRequest Request);

/// <summary>An ordered list of actions plus how the final screenshot should be taken.</summary>
public sealed record AgentBatchRequest(
    IReadOnlyList<AgentBatchStep> Steps,
    bool Screenshot,
    int SettleMilliseconds,
    AgentRequest ScreenshotOptions);

/// <summary>
/// Parses a batch of actions. The accepted shape is OpenAI's computer-use <c>actions[]</c>
/// (click, double_click, scroll, keypress, type, drag, move, wait, screenshot) so a model's
/// <c>computer_call</c> can be forwarded unchanged, extended with AgentSeat's own actions
/// (windows, focus_window, close_window, launch, info, zoom), held input (key_down, key_up,
/// mouse_down, mouse_up, hold, long_press, release, plus Anthropic's hold_key, left_mouse_down and
/// left_mouse_up) and the native spellings (<c>key</c>, <c>mouse_move</c>, <c>clicks</c>).
/// </summary>
public static class AgentActionBatch
{
    public const int MaxActions = 50;
    public const int DefaultSettleMilliseconds = 500;
    public const int MaxSettleMilliseconds = 5000;
    public const int DefaultWaitMilliseconds = 1000;
    private const int PixelsPerWheelNotch = 100;

    /// <summary>
    /// Accepts a bare action array, an object with an <c>actions</c> array and optional
    /// <c>screenshot</c>, <c>settleMs</c>, <c>scale</c>, <c>format</c>, <c>quality</c>, <c>cursor</c>,
    /// <c>region</c>, <c>diff</c>, <c>since</c> and <c>stableMs</c> settings, or a single action object.
    /// </summary>
    public static bool TryParse(JsonNode? root, out AgentBatchRequest? batch, out string? error)
    {
        batch = null;
        error = null;
        JsonArray actions;
        var options = new JsonObject();
        switch (root)
        {
            case JsonArray array:
                actions = array;
                break;
            case JsonObject { } obj when obj["actions"] is JsonArray nested:
                actions = nested;
                options = obj;
                break;
            case JsonObject { } single when single.ContainsKey("type") || single.ContainsKey("action"):
                actions = new JsonArray { single.DeepClone() };
                break;
            default:
                error = "Expected an action array, an object with an 'actions' array, or a single action object.";
                return false;
        }

        if (actions.Count > MaxActions)
        {
            error = $"At most {MaxActions} actions can be sent in one batch.";
            return false;
        }

        var wantScreenshot = ReadBool(options, "screenshot") ?? true;
        var settle = ReadInt(options, "settleMs") ?? DefaultSettleMilliseconds;
        if (settle is < 0 or > MaxSettleMilliseconds)
        {
            error = $"settleMs must be between 0 and {MaxSettleMilliseconds}.";
            return false;
        }

        AgentRegion? region;
        try
        {
            region = ReadRegion(options);
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return false;
        }

        var steps = new List<AgentBatchStep>();
        for (var index = 0; index < actions.Count; index++)
        {
            if (actions[index] is not JsonObject action)
            {
                error = $"Action {index} must be an object.";
                return false;
            }

            if (!TryTranslate(action, out var requests, out var explicitScreenshot, ref region, out var translateError))
            {
                error = $"Action {index}: {translateError}";
                return false;
            }

            wantScreenshot |= explicitScreenshot;
            foreach (var request in requests)
            {
                var problem = AgentRequestValidator.Validate(request);
                if (problem is not null)
                {
                    error = $"Action {index} ('{request.Action}'): {problem}";
                    return false;
                }

                steps.Add(new AgentBatchStep(index, request));
            }
        }

        var screenshotOptions = new AgentRequest
        {
            Action = AgentActions.Screenshot,
            Scale = ReadDouble(options, "scale"),
            Format = ReadString(options, "format"),
            Quality = ReadInt(options, "quality"),
            Cursor = ReadBool(options, "cursor"),
            Region = region,
            // Change reports compare whole screens, so a zoomed (region) capture never takes part.
            Diff = region is null ? ReadBool(options, "diff") : null,
            Since = region is null ? ReadString(options, "since") : null,
            StableMilliseconds = ReadInt(options, "stableMs")
        };
        var screenshotProblem = AgentRequestValidator.Validate(screenshotOptions);
        if (screenshotProblem is not null)
        {
            error = screenshotProblem;
            return false;
        }

        batch = new AgentBatchRequest(steps, wantScreenshot, settle, screenshotOptions);
        return true;
    }

    private static bool TryTranslate(
        JsonObject action,
        out List<AgentRequest> requests,
        out bool explicitScreenshot,
        ref AgentRegion? region,
        out string? error)
    {
        requests = [];
        explicitScreenshot = false;
        error = null;
        var type = (ReadString(action, "type") ?? ReadString(action, "action"))?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(type))
        {
            error = "Missing 'type'.";
            return false;
        }

        try
        {
            switch (type)
            {
                case "screenshot":
                    explicitScreenshot = true;
                    return true;
                case "zoom":
                    // Anthropic's zoom: the batch's screenshot shows only this part of the screen, at full resolution.
                    region = ReadRegion(action) ?? throw new InvalidOperationException(
                        "Zoom needs region [x0, y0, x1, y1] or x, y, width, height.");
                    explicitScreenshot = true;
                    return true;
                case "click":
                case "double_click":
                    return TranslateClick(action, type == "double_click", requests, out error);
                case "move":
                case AgentActions.MouseMove:
                    requests.Add(new AgentRequest
                    {
                        Action = AgentActions.MouseMove,
                        X = ReadInt(action, "x"),
                        Y = ReadInt(action, "y")
                    });
                    return true;
                case "drag":
                    requests.Add(TranslateDrag(action));
                    return true;
                case "scroll":
                    return TranslateScroll(action, requests, out error);
                case "type":
                    requests.Add(new AgentRequest { Action = AgentActions.Type, Text = ReadString(action, "text") });
                    return true;
                case "keypress":
                case "key":
                    requests.Add(new AgentRequest
                    {
                        Action = AgentActions.Key,
                        Keys = ReadKeys(action),
                        Repeat = ReadInt(action, "repeat")
                    });
                    return true;
                case AgentActions.KeyDown:
                case "keydown":
                case AgentActions.KeyUp:
                case "keyup":
                    requests.Add(new AgentRequest
                    {
                        Action = type is AgentActions.KeyDown or "keydown" ? AgentActions.KeyDown : AgentActions.KeyUp,
                        Keys = ReadKeys(action) ?? ReadString(action, "text")
                    });
                    return true;
                case AgentActions.MouseDown:
                case AgentActions.MouseUp:
                case "left_mouse_down":
                case "left_mouse_up":
                    requests.Add(new AgentRequest
                    {
                        Action = type is AgentActions.MouseDown or "left_mouse_down" ? AgentActions.MouseDown : AgentActions.MouseUp,
                        X = ReadInt(action, "x"),
                        Y = ReadInt(action, "y"),
                        Button = type is "left_mouse_down" or "left_mouse_up" ? "left" : ReadString(action, "button")
                    });
                    return true;
                case AgentActions.Hold:
                case "hold_key":
                case "long_press":
                    requests.Add(new AgentRequest
                    {
                        Action = AgentActions.Hold,
                        Keys = type == "long_press" ? null : ReadKeys(action) ?? ReadString(action, "text"),
                        Button = ReadString(action, "button") ?? (type == "long_press" ? "left" : null),
                        X = ReadInt(action, "x"),
                        Y = ReadInt(action, "y"),
                        Milliseconds = ReadInt(action, "ms") ?? ReadInt(action, "milliseconds") ?? SecondsAsMilliseconds(action)
                    });
                    return true;
                case AgentActions.Release:
                case "release_all":
                    requests.Add(new AgentRequest { Action = AgentActions.Release });
                    return true;
                case "wait":
                    requests.Add(new AgentRequest
                    {
                        Action = AgentActions.Wait,
                        Milliseconds = ReadInt(action, "ms") ?? ReadInt(action, "milliseconds") ?? DefaultWaitMilliseconds
                    });
                    return true;
                case AgentActions.Info:
                case AgentActions.Windows:
                case AgentActions.Observe:
                case AgentActions.FocusWindow:
                case AgentActions.CloseWindow:
                case AgentActions.Launch:
                    var native = (JsonObject)action.DeepClone();
                    native["action"] = type;
                    native.Remove("type");
                    var request = native.Deserialize<AgentRequest>(AgentJson.Options);
                    if (request is null)
                    {
                        error = "The action could not be read.";
                        return false;
                    }

                    requests.Add(request);
                    return true;
                default:
                    error = $"Unknown action type '{type}'.";
                    return false;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                              or FormatException or OverflowException)
        {
            error = $"Invalid '{type}' action: {exception.Message}";
            return false;
        }
    }

    private static bool TranslateClick(JsonObject action, bool doubleClick, List<AgentRequest> requests, out string? error)
    {
        error = null;
        var button = (ReadString(action, "button") ?? "left").ToLowerInvariant();
        switch (button)
        {
            case "back" or "forward":
                requests.Add(new AgentRequest
                {
                    Action = AgentActions.Key,
                    Keys = button == "back" ? "alt+left" : "alt+right"
                });
                return true;
            case "left" or "right" or "middle" or "wheel":
                requests.Add(new AgentRequest
                {
                    Action = AgentActions.Click,
                    X = ReadInt(action, "x"),
                    Y = ReadInt(action, "y"),
                    Button = button == "wheel" ? "middle" : button,
                    Clicks = doubleClick ? 2 : ReadInt(action, "clicks"),
                    Modifiers = ReadStrings(action, "modifiers") ?? ReadStrings(action, "keys")
                });
                return true;
            default:
                error = $"Unknown mouse button '{button}'.";
                return false;
        }
    }

    private static AgentRequest TranslateDrag(JsonObject action)
    {
        if (action["path"] is JsonArray path)
        {
            return new AgentRequest
            {
                Action = AgentActions.Drag,
                Button = ReadString(action, "button"),
                Modifiers = ReadStrings(action, "modifiers") ?? ReadStrings(action, "keys"),
                Points = path
                    .Select(node => node as JsonObject ??
                                    throw new InvalidOperationException("Every drag path entry needs x and y."))
                    .Select(point => new AgentPoint(
                        ReadInt(point, "x") ?? throw new InvalidOperationException("Drag path entries need x."),
                        ReadInt(point, "y") ?? throw new InvalidOperationException("Drag path entries need y.")))
                    .ToArray()
            };
        }

        return new AgentRequest
        {
            Action = AgentActions.Drag,
            X = ReadInt(action, "x"),
            Y = ReadInt(action, "y"),
            EndX = ReadInt(action, "endX"),
            EndY = ReadInt(action, "endY"),
            Button = ReadString(action, "button"),
            Modifiers = ReadStrings(action, "modifiers") ?? ReadStrings(action, "keys")
        };
    }

    private static bool TranslateScroll(JsonObject action, List<AgentRequest> requests, out string? error)
    {
        error = null;
        var x = ReadInt(action, "x");
        var y = ReadInt(action, "y");
        var modifiers = ReadStrings(action, "modifiers") ?? ReadStrings(action, "keys");
        if (ReadString(action, "direction") is { } direction)
        {
            requests.Add(new AgentRequest
            {
                Action = AgentActions.Scroll,
                X = x,
                Y = y,
                Direction = direction,
                Amount = ReadInt(action, "amount"),
                Modifiers = modifiers
            });
            return true;
        }

        // OpenAI expresses scrolling as pixel offsets; positive y scrolls down, positive x right.
        var scrollX = ReadInt(action, "scroll_x") ?? ReadInt(action, "scrollX") ?? 0;
        var scrollY = ReadInt(action, "scroll_y") ?? ReadInt(action, "scrollY") ?? 0;
        if (scrollX == 0 && scrollY == 0)
        {
            error = "Scroll needs scroll_x/scroll_y offsets or a direction.";
            return false;
        }

        if (scrollY != 0)
        {
            requests.Add(WheelRequest(x, y, scrollY > 0 ? "down" : "up", scrollY, modifiers));
        }

        if (scrollX != 0)
        {
            requests.Add(WheelRequest(x, y, scrollX > 0 ? "right" : "left", scrollX, modifiers));
        }

        return true;
    }

    private static AgentRequest WheelRequest(int? x, int? y, string direction, int pixels, string[]? modifiers) => new()
    {
        Action = AgentActions.Scroll,
        X = x,
        Y = y,
        Direction = direction,
        Amount = Math.Clamp(
            // Math.Abs(int.MinValue) throws, so widen first.
            (int)Math.Min(Math.Round(Math.Abs((long)pixels) / (double)PixelsPerWheelNotch, MidpointRounding.AwayFromZero), int.MaxValue),
            1,
            AgentRequestValidator.MaxScrollAmount),
        Modifiers = modifiers
    };

    /// <summary>OpenAI sends <c>keys: ["CTRL","A"]</c> (one chord); native callers send <c>keys: "ctrl+a Enter"</c>.</summary>
    private static string? ReadKeys(JsonObject action)
    {
        if (action["keys"] is JsonArray keys)
        {
            return string.Join('+', keys.Select(key =>
            {
                var name = key?.GetValue<string>() ?? string.Empty;
                return name == " " ? "space" : name;
            }));
        }

        return ReadString(action, "keys") ?? ReadString(action, "key");
    }

    /// <summary>
    /// A screen area given as Anthropic's <c>region: [x0, y0, x1, y1]</c> corners, as
    /// <c>region: {x, y, width, height}</c>, or as x, y, width, height fields on a zoom action; null when absent.
    /// </summary>
    private static AgentRegion? ReadRegion(JsonObject source)
    {
        switch (source["region"])
        {
            case JsonArray corners:
                var values = corners.Select(ToInt).ToArray();
                if (values.Length != 4 || values.Any(value => value is null))
                {
                    throw new InvalidOperationException("region must be four numbers [x0, y0, x1, y1].");
                }

                if (values[2] <= values[0] || values[3] <= values[1])
                {
                    throw new InvalidOperationException("region [x0, y0, x1, y1] needs x1 > x0 and y1 > y0.");
                }

                return new AgentRegion(values[0]!.Value, values[1]!.Value, values[2]!.Value - values[0]!.Value, values[3]!.Value - values[1]!.Value);
            case JsonObject box:
                return SizedRegion(box);
            case null:
                return source.ContainsKey("width") || source.ContainsKey("height") ? SizedRegion(source) : null;
            default:
                throw new InvalidOperationException("region must be [x0, y0, x1, y1] or {x, y, width, height}.");
        }
    }

    private static AgentRegion SizedRegion(JsonObject box) => new(
        ReadInt(box, "x") ?? throw new InvalidOperationException("region needs x."),
        ReadInt(box, "y") ?? throw new InvalidOperationException("region needs y."),
        ReadInt(box, "width") ?? throw new InvalidOperationException("region needs width."),
        ReadInt(box, "height") ?? throw new InvalidOperationException("region needs height."));

    /// <summary>Anthropic's hold_key gives <c>duration</c> in seconds.</summary>
    private static int? SecondsAsMilliseconds(JsonObject action) =>
        ReadDouble(action, "duration") is { } seconds
            ? (int)Math.Clamp(Math.Round(seconds * 1000, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue)
            : null;

    private static string? ReadString(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string[]? ReadStrings(JsonObject source, string name) =>
        source[name] is JsonArray array
            ? array.Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty)
                .ToArray()
            : null;

    private static bool? ReadBool(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static double? ReadDouble(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static int? ReadInt(JsonObject source, string name) => ToInt(source[name]);

    /// <summary>Models sometimes emit 135.0 or 135.4 for pixel coordinates, so numbers are rounded.</summary>
    private static int? ToInt(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<double>(out var number) ||
            double.IsNaN(number) || double.IsInfinity(number))
        {
            return null;
        }

        var rounded = Math.Round(number, MidpointRounding.AwayFromZero);
        return rounded is < int.MinValue or > int.MaxValue ? null : (int)rounded;
    }
}
