using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentSeat.Core.Agent;

/// <summary>Every operation the agent API can perform inside a seat's Windows session.</summary>
public static class AgentActions
{
    public const string Info = "info";
    public const string Screenshot = "screenshot";
    public const string MouseMove = "mouse_move";
    public const string Click = "click";
    public const string Drag = "drag";
    public const string Scroll = "scroll";
    public const string Type = "type";
    public const string Key = "key";
    public const string Windows = "windows";
    public const string Observe = "observe";
    public const string FocusWindow = "focus_window";
    public const string CloseWindow = "close_window";
    public const string Launch = "launch";
    public const string Wait = "wait";

    /// <summary>Presses keys and leaves them down across commands until <see cref="KeyUp"/> or <see cref="Release"/>.</summary>
    public const string KeyDown = "key_down";

    public const string KeyUp = "key_up";

    /// <summary>Presses a mouse button and leaves it down across commands until <see cref="MouseUp"/> or <see cref="Release"/>.</summary>
    public const string MouseDown = "mouse_down";

    public const string MouseUp = "mouse_up";

    /// <summary>Holds keys and/or a mouse button for a fixed time inside one command, then always lets go.</summary>
    public const string Hold = "hold";

    /// <summary>Lets go of every key and button that key_down or mouse_down left held.</summary>
    public const string Release = "release";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Info, Screenshot, MouseMove, Click, Drag, Scroll, Type, Key, Windows, Observe, FocusWindow, CloseWindow, Launch, Wait,
        KeyDown, KeyUp, MouseDown, MouseUp, Hold, Release
    };

    /// <summary>Actions that can leave input held in the session after they return.</summary>
    public static bool CanLeaveInputHeld(string action) => action is KeyDown or MouseDown;
}

public sealed record AgentRegion(int X, int Y, int Width, int Height);

public sealed record AgentPoint(int X, int Y);

/// <summary>
/// One agent command. The same flat shape travels REST client → service → helper, so each
/// action only reads the fields it documents and ignores the rest. All coordinates are real
/// desktop pixels of the seat session (the virtual screen origin may be negative).
/// </summary>
public sealed record AgentRequest
{
    public string Action { get; init; } = string.Empty;

    public int? X { get; init; }

    public int? Y { get; init; }

    public int? EndX { get; init; }

    public int? EndY { get; init; }

    /// <summary>Drag path through two or more points; replaces x/y/endX/endY when present.</summary>
    public AgentPoint[]? Points { get; init; }

    /// <summary><c>left</c> (default), <c>right</c> or <c>middle</c>.</summary>
    public string? Button { get; init; }

    /// <summary>1 (default) to 3 consecutive clicks.</summary>
    public int? Clicks { get; init; }

    /// <summary>Scroll direction: <c>up</c>, <c>down</c>, <c>left</c> or <c>right</c>.</summary>
    public string? Direction { get; init; }

    /// <summary>Mouse-wheel notches for <c>scroll</c>.</summary>
    public int? Amount { get; init; }

    public string? Text { get; init; }

    /// <summary>Space-separated key chords such as <c>ctrl+shift+t</c> or <c>ctrl+a Delete</c>.</summary>
    public string? Keys { get; init; }

    /// <summary>How many times <c>key</c> plays its whole chord sequence (default 1).</summary>
    public int? Repeat { get; init; }

    /// <summary>Modifiers held during click, drag or scroll: ctrl, shift, alt, win.</summary>
    public string[]? Modifiers { get; init; }

    public long? Handle { get; init; }

    /// <summary>Bounded UI Automation observation; defaults to 60 elements and 4000 text characters.</summary>
    public int? MaxElements { get; init; }

    public int? MaxCharacters { get; init; }

    public string? Path { get; init; }

    public string[]? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Screenshot output scale from 0.05 to 1 (default 1).</summary>
    public double? Scale { get; init; }

    /// <summary><c>png</c> (default) or <c>jpeg</c>.</summary>
    public string? Format { get; init; }

    public int? Quality { get; init; }

    /// <summary>Draw the mouse cursor into screenshots (default true).</summary>
    public bool? Cursor { get; init; }

    public AgentRegion? Region { get; init; }

    /// <summary>Wait time for <c>wait</c>; hold time for <c>hold</c>.</summary>
    public int? Milliseconds { get; init; }

    /// <summary>Screenshot: also return the frame's <see cref="AgentFrameSignature"/> (full-screen captures only).</summary>
    public bool? Diff { get; init; }

    /// <summary>Screenshot: signature of the frame the caller saw last; the response then says what changed since.</summary>
    public string? Since { get; init; }

    /// <summary>Screenshot: wait up to this long for the screen to stop changing before capturing.</summary>
    public int? StableMilliseconds { get; init; }
}

public sealed record AgentResponse
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public string? ErrorCode { get; init; }

    public JsonObject? Data { get; init; }

    public static AgentResponse Success(JsonObject? data = null) => new() { Ok = true, Data = data };

    public static AgentResponse Failure(string code, string message) =>
        new() { Ok = false, ErrorCode = code, Error = message };
}

/// <summary>
/// A captured screen. <see cref="Region"/> is set for a region (zoom) capture. For a full-screen capture taken with
/// <see cref="AgentRequest.Diff"/>, <see cref="Signature"/> fingerprints the frame, and when the request named the
/// frame the caller saw last, <see cref="Changed"/> and <see cref="Changes"/> say what is different now.
/// </summary>
public sealed record AgentScreenshot(
    string MimeType,
    int Width,
    int Height,
    string DataBase64,
    AgentRegion? Region = null,
    string? Signature = null,
    bool? Changed = null,
    IReadOnlyList<AgentScreenshotChange>? Changes = null);

/// <summary>
/// One area of the screen that changed, in real screen pixels. When it is small enough to be worth it, a
/// full-resolution crop of the area comes with it: crop pixel (cx, cy) is screen pixel (X + cx, Y + cy).
/// </summary>
public sealed record AgentScreenshotChange(int X, int Y, int Width, int Height, string? MimeType = null, string? DataBase64 = null);

/// <summary>Outcome of one executed step; <see cref="Index"/> refers to the action in the submitted batch.</summary>
public sealed record AgentActionResult(
    int Index,
    string Action,
    bool Ok,
    string? Error,
    string? ErrorCode,
    JsonObject? Data);

/// <summary>
/// What a batch returns, mirroring the computer-use loop: every action that ran, then one
/// screenshot of the resulting screen. A failed action stops the batch but the screenshot of
/// where it stopped is still returned.
/// </summary>
public sealed record AgentBatchResult(
    bool Ok,
    int Completed,
    int? FailedIndex,
    string? Error,
    string? ErrorCode,
    IReadOnlyList<AgentActionResult> Results,
    AgentScreenshot? Screenshot,
    string? ScreenshotError);

public sealed record AgentStatus(
    string SeatId,
    bool AgentControlEnabled,
    bool SessionAvailable,
    int? SessionId,
    bool HelperRunning,
    int? HelperProcessId,
    bool Paused,
    int? DesktopWidth,
    int? DesktopHeight,
    DateTimeOffset? LastActionUtc,
    string Detail,
    string? SharePath = null,
    // True when the seat cannot be agent-controlled at all (not approved by an administrator, switched off, or the
    // physical console); Detail says why.
    bool Refused = false);

/// <summary>What closing a seat did: the session that was logged off (null when none was open) and the share wipe.</summary>
public sealed record AgentCloseResult(bool Closed, int? SessionId, int FilesRemoved, int FilesFailed, string Detail);

public sealed record AgentShareClearResult(int Removed, int Failed);

public sealed record AgentHelperInfo(int ProcessId, int SessionId, DateTimeOffset StartedUtc);

/// <summary>A failure the agent API can describe to its caller with a stable machine-readable code.</summary>
public sealed class AgentControlException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public const string InvalidRequest = "invalid_request";
    public const string Disabled = "disabled";
    public const string Paused = "paused";
    public const string Busy = "busy";
    public const string SessionUnavailable = "session_unavailable";
    public const string HelperUnavailable = "helper_unavailable";
    public const string HelperFailed = "helper_failed";
    public const string Timeout = "timeout";
    public const string Stopped = "stopped";
    public const string ShareUnavailable = "share_unavailable";

    public string Code { get; } = code;
}

public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
