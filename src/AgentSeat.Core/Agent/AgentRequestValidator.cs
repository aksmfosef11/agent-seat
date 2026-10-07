namespace AgentSeat.Core.Agent;

/// <summary>
/// Rejects malformed agent commands before they reach the session helper. The service and the
/// helper both run this, so neither trusts the other's input.
/// </summary>
public static class AgentRequestValidator
{
    public const int MinCoordinate = -32768;
    public const int MaxCoordinate = 32767;
    public const int MaxTextLength = 10_000;
    public const int MaxWaitMilliseconds = 10_000;
    public const int MaxScrollAmount = 100;
    // A drag glides a few steps per segment at ~15 ms each; 50 points stays well inside the command timeout.
    public const int MaxDragPoints = 50;
    public const int MaxKeysLength = 256;
    // A hold runs inside one helper command; the owner's pause only lands between commands, so keep it short.
    public const int MaxHoldMilliseconds = 10_000;
    public const int MaxRepeat = 100;
    // ~50 ms per chord: keeps one key command far inside the helper command timeout.
    public const int MaxKeyPresses = 200;
    public const int MaxStableMilliseconds = 5000;

    private static readonly HashSet<string> Buttons = new(StringComparer.OrdinalIgnoreCase)
    {
        "left", "right", "middle"
    };

    private static readonly HashSet<string> Directions = new(StringComparer.OrdinalIgnoreCase)
    {
        "up", "down", "left", "right"
    };

    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpeg", "jpg"
    };

    /// <summary>Returns a human-readable problem, or <see langword="null"/> when the request is valid.</summary>
    public static string? Validate(AgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AgentActions.All.Contains(request.Action))
        {
            return $"Unknown action '{request.Action}'.";
        }

        return request.Action switch
        {
            AgentActions.Info or AgentActions.Windows or AgentActions.Release => null,
            AgentActions.Observe => ValidateObservation(request),
            AgentActions.Screenshot => ValidateScreenshot(request),
            AgentActions.MouseMove => RequirePoint(request.X, request.Y, "x", "y"),
            AgentActions.Click => ValidateClick(request),
            AgentActions.Drag => ValidateDrag(request),
            AgentActions.Scroll => ValidateScroll(request),
            AgentActions.Type => ValidateText(request),
            AgentActions.Key => ValidateKeys(request) ?? ValidateRepeat(request),
            AgentActions.KeyDown => ValidateKeys(request),
            AgentActions.KeyUp => request.Keys is null ? null : ValidateKeys(request),
            AgentActions.MouseDown or AgentActions.MouseUp =>
                ValidateOptionalPoint(request) ?? ValidateButton(request.Button),
            AgentActions.Hold => ValidateHold(request),
            AgentActions.FocusWindow or AgentActions.CloseWindow => ValidateHandle(request),
            AgentActions.Launch => ValidateLaunch(request),
            AgentActions.Wait => ValidateWait(request),
            _ => null
        };
    }

    private static string? ValidateScreenshot(AgentRequest request)
    {
        if (request.Scale is { } scale && !(scale is >= 0.05 and <= 1.0))
        {
            return "Scale must be between 0.05 and 1.";
        }

        if (request.Format is { } format && !Formats.Contains(format))
        {
            return "Format must be 'png' or 'jpeg'.";
        }

        if (request.Quality is { } quality && quality is < 1 or > 100)
        {
            return "Quality must be between 1 and 100.";
        }

        if (request.StableMilliseconds is { } stable && stable is < 0 or > MaxStableMilliseconds)
        {
            return $"stableMs must be between 0 and {MaxStableMilliseconds}.";
        }

        if (request.Since is { Length: > AgentFrameSignature.MaxEncodedLength })
        {
            return "since is too long to be a frame signature.";
        }

        if (request.Region is { } region)
        {
            if (region.Width is < 1 or > 16384 || region.Height is < 1 or > 16384)
            {
                return "Region width and height must be between 1 and 16384.";
            }

            return RequirePoint(region.X, region.Y, "region.x", "region.y");
        }

        return null;
    }

    private static string? ValidateObservation(AgentRequest request) =>
        request.Handle is <= 0 ? "Window handle must be positive." :
        request.MaxElements is < 1 or > 150 ? "maxElements must be between 1 and 150." :
        request.MaxCharacters is < 256 or > 12000 ? "maxCharacters must be between 256 and 12000." : null;

    private static string? ValidateClick(AgentRequest request) =>
        ValidateOptionalPoint(request) ??
        ValidateButton(request.Button) ??
        ValidateClicks(request.Clicks) ??
        ValidateModifiers(request.Modifiers);

    /// <summary>x and y together (act there) or neither (act at the current pointer position).</summary>
    private static string? ValidateOptionalPoint(AgentRequest request)
    {
        if ((request.X is null) != (request.Y is null))
        {
            return "Provide both x and y, or neither to use the current pointer position.";
        }

        return request.X is null ? null : RequirePoint(request.X, request.Y, "x", "y");
    }

    private static string? ValidateRepeat(AgentRequest request)
    {
        if (request.Repeat is null)
        {
            return null;
        }

        if (request.Repeat is < 1 or > MaxRepeat)
        {
            return $"Repeat must be between 1 and {MaxRepeat}.";
        }

        // ValidateKeys already proved the sequence parses.
        _ = AgentKeyMap.TryParseSequence(request.Keys, out var chords, out _);
        return chords.Count * request.Repeat.Value > MaxKeyPresses
            ? $"A key command may press at most {MaxKeyPresses} chords in total (chords × repeat)."
            : null;
    }

    private static string? ValidateHold(AgentRequest request)
    {
        if (request.Milliseconds is not (>= 1 and <= MaxHoldMilliseconds))
        {
            return $"Hold time must be between 1 and {MaxHoldMilliseconds} ms. Hold longer with key_down / mouse_down, wait and key_up / mouse_up.";
        }

        if (request.Keys is null && request.Button is null)
        {
            return "Hold needs keys, a mouse button, or both.";
        }

        return (request.Keys is null ? null : ValidateKeys(request)) ??
               ValidateButton(request.Button) ??
               ValidateOptionalPoint(request);
    }

    private static string? ValidateDrag(AgentRequest request)
    {
        string? path;
        if (request.Points is { } points)
        {
            if (points.Length is < 2 or > MaxDragPoints)
            {
                return $"A drag path needs between 2 and {MaxDragPoints} points.";
            }

            path = points.Select(point => point is null
                    ? "Drag path points must not be null."
                    : RequirePoint(point.X, point.Y, "path x", "path y"))
                .FirstOrDefault(problem => problem is not null);
        }
        else
        {
            path = RequirePoint(request.X, request.Y, "x", "y") ??
                   RequirePoint(request.EndX, request.EndY, "endX", "endY");
        }

        return path ?? ValidateButton(request.Button) ?? ValidateModifiers(request.Modifiers);
    }

    private static string? ValidateScroll(AgentRequest request)
    {
        if (request.Direction is null || !Directions.Contains(request.Direction))
        {
            return "Direction must be up, down, left or right.";
        }

        if (request.Amount is { } amount && amount is < 1 or > MaxScrollAmount)
        {
            return $"Amount must be between 1 and {MaxScrollAmount}.";
        }

        return ValidateOptionalPoint(request) ?? ValidateModifiers(request.Modifiers);
    }

    private static string? ValidateText(AgentRequest request)
    {
        if (string.IsNullOrEmpty(request.Text))
        {
            return "Text must not be empty.";
        }

        if (request.Text.Length > MaxTextLength)
        {
            return $"Text is limited to {MaxTextLength} characters per command.";
        }

        return request.Text.Contains('\0') ? "Text must not contain NUL characters." : null;
    }

    private static string? ValidateKeys(AgentRequest request)
    {
        if (request.Keys is { Length: > MaxKeysLength })
        {
            return $"Keys is limited to {MaxKeysLength} characters.";
        }

        return AgentKeyMap.TryParseSequence(request.Keys, out _, out var error) ? null : error;
    }

    private static string? ValidateHandle(AgentRequest request) =>
        request.Handle is > 0 ? null : "A positive window handle is required.";

    private static string? ValidateLaunch(AgentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path) || request.Path.Length > 2048 ||
            request.Path.Any(char.IsControl))
        {
            return "Path must be a non-empty string of at most 2048 characters without control characters.";
        }

        if (request.Arguments is { } arguments &&
            (arguments.Length > 64 || arguments.Any(argument => argument is null || argument.Length > 4096 ||
                                                               argument.Contains('\0'))))
        {
            return "At most 64 arguments of 4096 characters each are allowed.";
        }

        if (request.WorkingDirectory is { } directory &&
            (directory.Length > 2048 || directory.Any(char.IsControl)))
        {
            return "Working directory is invalid.";
        }

        return null;
    }

    private static string? ValidateWait(AgentRequest request) =>
        request.Milliseconds is >= 1 and <= MaxWaitMilliseconds
            ? null
            : $"Milliseconds must be between 1 and {MaxWaitMilliseconds}.";

    private static string? ValidateButton(string? button) =>
        button is null || Buttons.Contains(button) ? null : "Button must be left, right or middle.";

    private static string? ValidateClicks(int? clicks) =>
        clicks is null or >= 1 and <= 3 ? null : "Clicks must be between 1 and 3.";

    private static string? ValidateModifiers(string[]? modifiers)
    {
        if (modifiers is null)
        {
            return null;
        }

        foreach (var modifier in modifiers)
        {
            if (modifier is null || !AgentKeyMap.IsModifierName(modifier))
            {
                return $"Unknown modifier '{modifier}'. Use ctrl, shift, alt or win.";
            }
        }

        return null;
    }

    private static string? RequirePoint(int? x, int? y, string xName, string yName)
    {
        if (x is null || y is null)
        {
            return $"{xName} and {yName} are required.";
        }

        return x is < MinCoordinate or > MaxCoordinate || y is < MinCoordinate or > MaxCoordinate
            ? $"{xName} and {yName} must be between {MinCoordinate} and {MaxCoordinate}."
            : null;
    }
}
