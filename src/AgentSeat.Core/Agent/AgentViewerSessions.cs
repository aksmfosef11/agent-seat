using System.Security.Cryptography;

namespace AgentSeat.Core.Agent;

/// <summary>One-use browser tickets and seat-bound sessions. No bearer token enters a URL or browser storage.</summary>
public sealed class AgentViewerSessions(TimeProvider? timeProvider = null)
{
    public const string CookieName = "agent-seat-viewer";
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _tickets = [];
    private readonly Dictionary<string, Entry> _sessions = [];
    private sealed record Entry(string Seat, string Fingerprint, DateTimeOffset Expires);
    public sealed record BrowserSession(string Seat, string Cookie);

    public string Issue(string seat, string fingerprint)
    {
        lock (_gate)
        {
            Prune();
            if (_tickets.Count >= 128) throw new InvalidOperationException("Too many pending viewer tickets.");
            var ticket = Secret();
            _tickets[Hash(ticket)] = new Entry(seat, fingerprint, _clock.GetUtcNow().AddMinutes(1));
            return ticket;
        }
    }

    public BrowserSession? Redeem(string? ticket, string? currentFingerprint)
    {
        lock (_gate)
        {
            Prune();
            if (ticket is null || ticket.Length != 64 || !_tickets.Remove(Hash(ticket), out var entry) ||
                entry.Fingerprint != currentFingerprint || _sessions.Count >= 128) return null;
            var cookie = Secret();
            _sessions[Hash(cookie)] = entry with { Expires = _clock.GetUtcNow().AddMinutes(30) };
            return new BrowserSession(entry.Seat, cookie);
        }
    }

    public bool Authorize(string? cookie, string seat, string? currentFingerprint)
    {
        lock (_gate)
        {
            Prune();
            return cookie is { Length: 64 } && _sessions.TryGetValue(Hash(cookie), out var entry) &&
                   entry.Seat == seat && entry.Fingerprint == currentFingerprint;
        }
    }

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var entries in new[] { _tickets, _sessions })
            foreach (var key in entries.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray())
                entries.Remove(key);
    }
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
