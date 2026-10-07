using System.Runtime.InteropServices;
using System.Text.Json;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;

namespace AgentSeat.Windows;

public sealed record AgentSeatPolicyOptions(string AllowlistPath)
{
    /// <summary>
    /// Lets an agent drive the physical console session. Off by default: that session is the owner's own
    /// desktop, which is exactly what agent seats exist to keep an agent away from. It is a setting, not an
    /// API field, so only someone who can edit the service configuration can turn it on (useful for testing).
    /// </summary>
    public bool AllowConsoleSession { get; init; }
}

/// <summary>
/// Reads the administrator-owned allow list (<c>agent-seats.json</c>, written only by elevated scripts):
/// <c>{ "seats": [ { "seatId": "agent", "userName": "seat-agent" } ] }</c>. A seat is permitted only when its
/// id AND current Windows user name both match an entry, so editing a seat through the management API (which
/// an agent can reach) can neither add a seat nor repoint an approved one at a different account.
/// </summary>
public sealed class AgentSeatPolicy : IAgentSeatPolicy
{
    private readonly AgentSeatPolicyOptions _options;
    private readonly Func<int> _consoleSessionId;
    private readonly object _gate = new();
    private DateTime _stamp;
    private long _length = -1;
    private IReadOnlyList<Entry> _entries = [];

    public AgentSeatPolicy(AgentSeatPolicyOptions options, Func<int>? consoleSessionId = null)
    {
        _options = options;
        _consoleSessionId = consoleSessionId ?? (() => NativeMethods.WTSGetActiveConsoleSessionId());
    }

    public string? Refusal(SeatDefinition seat)
    {
        var entry = Load().FirstOrDefault(candidate =>
            string.Equals(candidate.SeatId, seat.Id, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return $"Seat '{seat.Id}' is not approved for agent control. An administrator must run " +
                   $"scripts\\Enable-AgentControl.ps1 -AllowSeat {seat.Id} -AllowUser <windows-user> -Apply.";
        }

        return string.Equals(
            NormalizeUserName(entry.UserName),
            NormalizeUserName(seat.UserName),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Seat '{seat.Id}' was approved for a different Windows account than the one it is now configured with.";
    }

    public string? RefusalForSession(SeatSession session) =>
        !_options.AllowConsoleSession && session.SessionId == _consoleSessionId()
            ? "Refusing to drive the physical console session: that is the owner's own desktop. Agent seats must " +
              "run in their own session (AgentSeat:AgentAllowConsoleSession=true overrides this for testing)."
            : null;

    private IReadOnlyList<Entry> Load()
    {
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_options.AllowlistPath);
                if (!info.Exists)
                {
                    _entries = [];
                    _length = -1;
                    return _entries;
                }

                if (info.LastWriteTimeUtc != _stamp || info.Length != _length)
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(_options.AllowlistPath));
                    var entries = new List<Entry>();
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("seats", out var seats) &&
                        seats.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in seats.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object &&
                                item.TryGetProperty("seatId", out var id) && id.ValueKind == JsonValueKind.String &&
                                item.TryGetProperty("userName", out var user) && user.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(id.GetString()) && !string.IsNullOrWhiteSpace(user.GetString()))
                            {
                                entries.Add(new Entry(id.GetString()!, user.GetString()!));
                            }
                        }
                    }

                    _entries = entries;
                    _stamp = info.LastWriteTimeUtc;
                    _length = info.Length;
                }

                return _entries;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or JsonException or InvalidOperationException)
            {
                // An unreadable or malformed list approves nothing.
                _entries = [];
                _length = -1;
                return _entries;
            }
        }
    }

    private static string NormalizeUserName(string userName)
    {
        var slash = userName.LastIndexOf('\\');
        if (slash >= 0)
        {
            userName = userName[(slash + 1)..];
        }

        var at = userName.IndexOf('@');
        return (at > 0 ? userName[..at] : userName).Trim();
    }

    private sealed record Entry(string SeatId, string UserName);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern int WTSGetActiveConsoleSessionId();
    }
}
