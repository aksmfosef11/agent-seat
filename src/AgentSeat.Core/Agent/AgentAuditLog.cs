using System.Globalization;

namespace AgentSeat.Core.Agent;

public sealed record AgentAuditEntry(
    DateTimeOffset TimeUtc,
    string SeatId,
    string Action,
    string Summary,
    bool Ok,
    string? Error,
    long DurationMilliseconds);

/// <summary>In-memory ring buffer of what the agent did, so the owner can review it in the UI.</summary>
public sealed class AgentAuditLog(int capacity = 500)
{
    private readonly Queue<AgentAuditEntry> _entries = new();
    private readonly object _gate = new();

    public void Add(AgentAuditEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > capacity)
            {
                _ = _entries.Dequeue();
            }
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<AgentAuditEntry> Recent(string seatId, int count)
    {
        lock (_gate)
        {
            return _entries
                .Where(entry => string.Equals(entry.SeatId, seatId, StringComparison.OrdinalIgnoreCase))
                .Reverse()
                .Take(Math.Max(0, count))
                .ToArray();
        }
    }
}

/// <summary>Describes a command for the audit log without recording what the agent typed or launched with.</summary>
public static class AgentAuditSummary
{
    public static string Describe(AgentRequest request)
    {
        var culture = CultureInfo.InvariantCulture;
        return request.Action switch
        {
            AgentActions.MouseMove => string.Create(culture, $"move to ({request.X},{request.Y})"),
            AgentActions.Click => string.Create(culture,
                $"{request.Button ?? "left"} click x{request.Clicks ?? 1}{Position(request.X, request.Y)}"),
            AgentActions.Drag => request.Points is { Length: > 1 } points
                ? string.Create(culture,
                    $"drag ({points[0].X},{points[0].Y}) to ({points[^1].X},{points[^1].Y}) via {points.Length} points")
                : string.Create(culture, $"drag ({request.X},{request.Y}) to ({request.EndX},{request.EndY})"),
            AgentActions.Scroll => string.Create(culture,
                $"scroll {request.Direction} x{request.Amount ?? 3}{Position(request.X, request.Y)}"),
            AgentActions.Type => string.Create(culture, $"type {request.Text?.Length ?? 0} characters"),
            AgentActions.Key => request.Repeat is > 1
                ? string.Create(culture, $"key {Clip(request.Keys)} x{request.Repeat}")
                : $"key {Clip(request.Keys)}",
            AgentActions.KeyDown => $"key down {Clip(request.Keys)}",
            AgentActions.KeyUp => request.Keys is null ? "key up (all held)" : $"key up {Clip(request.Keys)}",
            AgentActions.MouseDown => $"{request.Button ?? "left"} button down{Position(request.X, request.Y)}",
            AgentActions.MouseUp => $"{request.Button ?? "left"} button up{Position(request.X, request.Y)}",
            AgentActions.Hold => string.Create(culture,
                $"hold {HoldTarget(request)} for {request.Milliseconds} ms{Position(request.X, request.Y)}"),
            AgentActions.Release => "release held keys and buttons",
            AgentActions.FocusWindow => string.Create(culture, $"focus window {request.Handle}"),
            AgentActions.CloseWindow => string.Create(culture, $"close window {request.Handle}"),
            AgentActions.Launch => $"launch {Clip(request.Path)}",
            AgentActions.Wait => string.Create(culture, $"wait {request.Milliseconds} ms"),
            AgentActions.Screenshot => request.Region is null ? "screenshot" : "screenshot (region)",
            _ => request.Action
        };
    }

    /// <summary>Keeps one log line bounded no matter what a caller (or a hostile helper) sends.</summary>
    public static string Clip(string? value, int maxLength = 200) =>
        value is null ? string.Empty : value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static string HoldTarget(AgentRequest request) => (request.Keys, request.Button) switch
    {
        ({ } keys, { } button) => $"{Clip(keys)} + {button} button",
        ({ } keys, null) => Clip(keys),
        (null, { } button) => $"{button} button",
        _ => "nothing"
    };

    private static string Position(int? x, int? y) =>
        x is null || y is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" at ({x},{y})");
}
