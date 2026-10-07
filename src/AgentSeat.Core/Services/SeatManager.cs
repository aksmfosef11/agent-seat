using System.Text.RegularExpressions;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;

namespace AgentSeat.Core.Services;

public sealed partial class SeatManager
{
    private const int FirstAutomaticSunshinePort = 47989;
    private const int SunshinePortStep = 100;

    private readonly ISeatStore _store;
    private readonly IWindowsSessionService _sessions;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public SeatManager(ISeatStore store, IWindowsSessionService sessions)
    {
        _store = store;
        _sessions = sessions;
    }

    public async Task<IReadOnlyList<SeatView>> GetViewsAsync(CancellationToken cancellationToken = default)
    {
        var seatsTask = _store.GetAllAsync(cancellationToken);
        var sessionsTask = _sessions.GetSessionsAsync(cancellationToken);
        await Task.WhenAll(seatsTask, sessionsTask).ConfigureAwait(false);

        var sessions = await sessionsTask.ConfigureAwait(false);
        return (await seatsTask.ConfigureAwait(false))
            .Select(seat => new SeatView(
                seat,
                sessions.Where(session => UserNamesMatch(seat.UserName, session)).ToArray()))
            .OrderBy(view => view.Seat.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<IReadOnlyList<SeatDefinition>> GetAllAsync(CancellationToken cancellationToken = default) =>
        _store.GetAllAsync(cancellationToken);

    public Task<SeatDefinition?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        _store.GetAsync(id, cancellationToken);

    public async Task<SeatDefinition> CreateAsync(
        SeatDefinition requested,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingSeats = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var seat = ValidateAndNormalize(requested) with { CreatedAtUtc = DateTimeOffset.UtcNow };
            seat = AssignAutomaticSunshinePort(seat, existingSeats);
            if (await _store.GetAsync(seat.Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new SeatValidationException($"Seat '{seat.Id}' already exists.");
            }

            EnsureUniqueResources(seat, existingSeats);
            await _store.UpsertAsync(seat, cancellationToken).ConfigureAwait(false);
            return seat;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<SeatDefinition> UpdateAsync(
        string id,
        SeatDefinition requested,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Seat '{id}' was not found.");

            var existingSeats = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var seat = ValidateAndNormalize(requested with
            {
                Id = id,
                CreatedAtUtc = existing.CreatedAtUtc,
                SunshineBasePort = requested.SunshineBasePort == 0
                    ? existing.SunshineBasePort
                    : requested.SunshineBasePort
            });
            seat = AssignAutomaticSunshinePort(
                seat,
                existingSeats.Where(candidate =>
                    !string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray());

            EnsureUniqueResources(seat, existingSeats);
            await _store.UpsertAsync(seat, cancellationToken).ConfigureAwait(false);
            return seat;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<SeatSession> RequireOwnedSessionAsync(
        string seatId,
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        var seat = await _store.GetAsync(seatId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Seat '{seatId}' was not found.");
        var session = (await _sessions.GetSessionsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.SessionId == sessionId)
            ?? throw new KeyNotFoundException($"Windows session '{sessionId}' was not found.");

        if (!UserNamesMatch(seat.UserName, session))
        {
            throw new SeatValidationException(
                $"Session '{sessionId}' does not belong to seat '{seatId}'.");
        }

        return session;
    }

    private static void EnsureUniqueResources(
        SeatDefinition candidate,
        IReadOnlyCollection<SeatDefinition> existingSeats)
    {
        var duplicate = existingSeats
            .FirstOrDefault(seat =>
                !string.Equals(seat.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeUserName(seat.UserName), NormalizeUserName(candidate.UserName),
                    StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            throw new SeatValidationException(
                $"Windows user '{candidate.UserName}' is already assigned to seat '{duplicate.Id}'.");
        }

        if (!candidate.StreamingEnabled)
        {
            return;
        }

        var candidatePorts = SunshinePorts.FromBasePort(candidate.SunshineBasePort).AllPorts.ToHashSet();
        var portConflict = existingSeats
            .Where(seat =>
                seat.StreamingEnabled &&
                seat.SunshineBasePort >= SunshinePorts.MinimumBasePort &&
                !string.Equals(seat.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
            .Select(seat => new
            {
                Seat = seat,
                Ports = SunshinePorts.FromBasePort(seat.SunshineBasePort).AllPorts
            })
            .FirstOrDefault(entry => entry.Ports.Any(candidatePorts.Contains));
        if (portConflict is not null)
        {
            var collision = portConflict.Ports.First(candidatePorts.Contains);
            throw new SeatValidationException(
                $"Sunshine port {collision} collides with seat '{portConflict.Seat.Id}'. Choose another base port.");
        }

        var rdpConflict = existingSeats
            .Append(candidate)
            .FirstOrDefault(seat => candidatePorts.Contains(seat.RdpPort));
        if (rdpConflict is not null)
        {
            throw new SeatValidationException(
                $"Sunshine's port family collides with configured RDP port {rdpConflict.RdpPort}.");
        }
    }

    private static SeatDefinition AssignAutomaticSunshinePort(
        SeatDefinition seat,
        IReadOnlyCollection<SeatDefinition> existingSeats)
    {
        if (!seat.StreamingEnabled || seat.SunshineBasePort != 0)
        {
            return seat;
        }

        var occupied = existingSeats
            .Where(candidate =>
                candidate.StreamingEnabled &&
                candidate.SunshineBasePort >= SunshinePorts.MinimumBasePort)
            .SelectMany(candidate => SunshinePorts.FromBasePort(candidate.SunshineBasePort).AllPorts)
            .Concat(existingSeats.Select(candidate => candidate.RdpPort))
            .ToHashSet();

        for (var basePort = FirstAutomaticSunshinePort;
             basePort <= SunshinePorts.MaximumBasePort;
             basePort += SunshinePortStep)
        {
            var ports = SunshinePorts.FromBasePort(basePort).AllPorts;
            if (ports.All(port => !occupied.Contains(port)) && !ports.Contains(seat.RdpPort))
            {
                return seat with { SunshineBasePort = basePort };
            }
        }

        throw new SeatValidationException("No collision-free Sunshine port family is available.");
    }

    private static SeatDefinition ValidateAndNormalize(SeatDefinition seat)
    {
        var id = seat.Id.Trim().ToLowerInvariant();
        var displayName = seat.DisplayName.Trim();
        var userName = seat.UserName.Trim();
        var hostAddress = seat.HostAddress.Trim();

        if (!SeatIdPattern().IsMatch(id))
        {
            throw new SeatValidationException(
                "Seat id must start with a letter and contain only lowercase letters, digits, or '-' (maximum 32 characters).");
        }

        if (displayName.Length is < 1 or > 80)
        {
            throw new SeatValidationException("Display name must contain 1 to 80 characters.");
        }

        if (userName.Length is < 1 or > 128 || ContainsLineBreak(userName))
        {
            throw new SeatValidationException("Windows user name is invalid.");
        }

        if (hostAddress.Length is < 1 or > 255 || ContainsLineBreak(hostAddress) || hostAddress.Any(char.IsWhiteSpace))
        {
            throw new SeatValidationException("Host address is invalid.");
        }

        if (seat.RdpPort is < 1 or > 65535)
        {
            throw new SeatValidationException("RDP port must be between 1 and 65535.");
        }

        if (seat.Width is < 800 or > 7680 || seat.Height is < 600 or > 4320)
        {
            throw new SeatValidationException("Resolution must be between 800x600 and 7680x4320.");
        }

        if (seat.StreamingEnabled && seat.SunshineBasePort != 0 &&
            seat.SunshineBasePort is < SunshinePorts.MinimumBasePort or > SunshinePorts.MaximumBasePort)
        {
            throw new SeatValidationException(
                $"Sunshine base port must be zero (automatic) or between {SunshinePorts.MinimumBasePort} and {SunshinePorts.MaximumBasePort}.");
        }

        return seat with
        {
            Id = id,
            DisplayName = displayName,
            UserName = userName,
            HostAddress = hostAddress
        };
    }

    private static bool UserNamesMatch(string configuredUserName, SeatSession session)
    {
        var normalized = NormalizeUserName(configuredUserName);
        return string.Equals(normalized, session.UserName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUserName(string userName)
    {
        var slash = userName.LastIndexOf('\\');
        if (slash >= 0)
        {
            userName = userName[(slash + 1)..];
        }

        var at = userName.IndexOf('@');
        return at > 0 ? userName[..at] : userName;
    }

    private static bool ContainsLineBreak(string value) =>
        value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal);

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SeatIdPattern();
}
