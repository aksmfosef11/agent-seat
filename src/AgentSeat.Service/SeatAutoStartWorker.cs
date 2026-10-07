using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;
using AgentSeat.Core.Services;

namespace AgentSeat.Service;

internal sealed class SeatAutoStartWorker(
    SeatManager manager,
    ISeatStreamingService streaming,
    ILogger<SeatAutoStartWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, AttemptState> _attempts =
        new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            await ReconcileAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SeatView> views;
        try
        {
            views = await manager.GetViewsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not enumerate seats for automatic streaming startup.");
            return;
        }

        var currentIds = views.Select(view => view.Seat.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var removed in _attempts.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            _attempts.Remove(removed);
        }

        foreach (var view in views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!view.Seat.Enabled || !view.Seat.StreamingEnabled || !view.Seat.AutoStartStreaming)
            {
                _attempts.Remove(view.Seat.Id);
                continue;
            }

            var session = view.Sessions
                .Where(candidate => candidate.State is
                    SeatSessionState.Active or SeatSessionState.Connected or SeatSessionState.Shadow)
                .OrderByDescending(candidate => candidate.State == SeatSessionState.Active)
                .ThenByDescending(candidate => candidate.SessionId)
                .FirstOrDefault();
            if (session is null)
            {
                _attempts.Remove(view.Seat.Id);
                continue;
            }

            if (_attempts.TryGetValue(view.Seat.Id, out var previous) &&
                previous.SessionId == session.SessionId &&
                (previous.Started || DateTimeOffset.UtcNow - previous.AttemptedAtUtc < TimeSpan.FromSeconds(15)))
            {
                continue;
            }

            try
            {
                var current = await streaming.GetStatusAsync(view.Seat, cancellationToken).ConfigureAwait(false);
                if (current.State is SunshineRuntimeState.Ready or SunshineRuntimeState.Starting)
                {
                    _attempts[view.Seat.Id] = new AttemptState(
                        session.SessionId,
                        DateTimeOffset.UtcNow,
                        Started: true);
                    continue;
                }

                _attempts[view.Seat.Id] = new AttemptState(
                    session.SessionId,
                    DateTimeOffset.UtcNow,
                    Started: false);
                var started = await streaming.StartAsync(view.Seat, session, cancellationToken).ConfigureAwait(false);
                var succeeded = started.State is SunshineRuntimeState.Ready or SunshineRuntimeState.Starting;
                _attempts[view.Seat.Id] = new AttemptState(
                    session.SessionId,
                    DateTimeOffset.UtcNow,
                    succeeded);
                if (succeeded)
                {
                    logger.LogInformation(
                        "Automatically started Sunshine for seat {SeatId} in WTS session {SessionId}; Moonlight address {Address}.",
                        view.Seat.Id,
                        session.SessionId,
                        started.MoonlightAddress);
                }
                else
                {
                    logger.LogWarning(
                        "Sunshine automatic startup for seat {SeatId} returned {State}: {Detail}",
                        view.Seat.Id,
                        started.State,
                        started.Detail);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _attempts[view.Seat.Id] = new AttemptState(
                    session.SessionId,
                    DateTimeOffset.UtcNow,
                    Started: false);
                logger.LogWarning(
                    exception,
                    "Could not automatically start Sunshine for seat {SeatId} in WTS session {SessionId}.",
                    view.Seat.Id,
                    session.SessionId);
            }
        }
    }

    private sealed record AttemptState(int SessionId, DateTimeOffset AttemptedAtUtc, bool Started);
}
