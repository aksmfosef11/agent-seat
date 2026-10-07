using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;
using AgentSeat.Core.Services;

namespace AgentSeat.Core.Agent;

public sealed record AgentControlOptions
{
    /// <summary>How long <c>start</c> waits for the hidden RDP anchor to create the seat session.</summary>
    public TimeSpan SessionStartTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan SessionPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for one helper command, screenshots included.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a batch waits behind the previous one before giving up.</summary>
    public TimeSpan QueueTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long close and clean wait for a running batch before refusing instead of cutting it off.</summary>
    public TimeSpan CloseGateTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long close waits for Windows to finish logging the seat session off.</summary>
    public TimeSpan LogoffTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Policy for agent control: only administrator-approved seats, one batch at a time per seat, an
/// owner-controlled pause and kill switch that also stop work already running or queued, and an audit
/// trail. The Windows mechanics live behind <see cref="IAgentHelperHost"/>.
/// </summary>
public sealed class AgentControlService(
    SeatManager manager,
    ISeatAnchorService anchors,
    IAgentHelperHost host,
    IAgentSeatPolicy policy,
    IWindowsSessionService sessions,
    IAgentFileShare share,
    AgentAuditLog audit,
    AgentControlOptions? options = null,
    TimeProvider? time = null) : IAgentControlService
{
    // Non-screenshot results are small by nature; anything larger is a misbehaving (or hostile) helper.
    private const int MaxResultCharacters = 1_000_000;
    private const int MaxErrorCharacters = 500;
    private const int MaxChangeRegions = 8;

    private readonly AgentControlOptions _options = options ?? new AgentControlOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SeatState> _states = new(StringComparer.OrdinalIgnoreCase);

    public async Task<AgentStatus> GetStatusAsync(SeatDefinition seat, CancellationToken cancellationToken = default)
    {
        var state = StateFor(seat);
        var refusal = SeatRefusal(seat);
        var found = refusal is null ? await FindSessionAsync(seat, cancellationToken).ConfigureAwait(false) : null;
        refusal ??= found is null ? null : policy.RefusalForSession(found);
        var session = refusal is null ? found : null;
        AgentHelperInfo? helper = null;
        if (session is not null)
        {
            helper = state.Helper is { } cached && cached.SessionId == session.SessionId && host.IsRunning(cached)
                ? cached
                : await host.TryAttachAsync(seat, session.SessionId, cancellationToken).ConfigureAwait(false);
            state.Helper = helper;
        }

        return BuildStatus(seat, state, session, helper, refusal, share.RootFor(seat));
    }

    public async Task<AgentStatus> StartAsync(SeatDefinition seat, CancellationToken cancellationToken = default)
    {
        EnsureEnabled(seat);
        var state = StateFor(seat);
        ThrowIfPaused(state);
        await AcquireGateAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfPaused(state);
            var session = await FindSessionAsync(seat, cancellationToken).ConfigureAwait(false) ??
                          await StartSeatSessionAsync(seat, cancellationToken).ConfigureAwait(false);
            RequireUsable(session);
            var helper = await EnsureHelperAsync(seat, state, session, cancellationToken).ConfigureAwait(false);
            try
            {
                var info = await host.SendAsync(
                        seat,
                        helper,
                        new AgentRequest { Action = AgentActions.Info },
                        cancellationToken)
                    .ConfigureAwait(false);
                RememberDesktop(state, info);
                RememberHeld(state, info); // a helper that outlived a service restart may still hold input
            }
            catch (AgentControlException exception) when (exception.Code is
                AgentControlException.HelperUnavailable or AgentControlException.HelperFailed)
            {
                state.Helper = null;
                throw;
            }

            return BuildStatus(seat, state, session, helper, refusal: null, share.RootFor(seat));
        }
        finally
        {
            _ = state.Gate.Release();
        }
    }

    public async Task<AgentStatus> StopAsync(SeatDefinition seat, CancellationToken cancellationToken = default)
    {
        // The kill switch never waits on the command gate and never talks to the helper: a frozen helper
        // must not be able to hold it up. Bumping the epoch also cancels a running batch between actions
        // and drops batches that are queued behind it.
        var state = StateFor(seat);
        state.Interrupt();
        var session = await FindSessionAsync(seat, cancellationToken).ConfigureAwait(false);
        var killed = state.Helper is { } known && host.Stop(known);
        var released = false;
        if (session is not null)
        {
            killed |= host.StopInSession(session.SessionId) > 0;
            // A killed helper never finishes its own clean-up (a hold, a click, what key_down / mouse_down held) and
            // the session has no physical keyboard to let go, so a one-shot helper releases whatever is left. That
            // also covers a second press of the kill switch, which kills the previous one-shot helper with the rest.
            if ((killed || state.InputsMayBeHeld) && policy.RefusalForSession(session) is null)
            {
                released = host.ReleaseInputs(seat, session.SessionId);
            }
        }

        if (released)
        {
            state.InputsMayBeHeld = false;
        }

        state.Helper = null;
        audit.Add(new AgentAuditEntry(
            _time.GetUtcNow(),
            seat.Id,
            "stop",
            released ? "helper stopped by owner, held input released" : "helper stopped by owner",
            true,
            null,
            0));
        return BuildStatus(seat, state, session, helper: null, refusal: null, share.RootFor(seat));
    }

    public async Task<AgentStatus> SetPausedAsync(
        SeatDefinition seat,
        bool paused,
        CancellationToken cancellationToken = default)
    {
        var state = StateFor(seat);
        state.Paused = paused;
        if (paused)
        {
            state.Interrupt(); // Stops a running batch at its next action and drops queued ones.
            // Always scheduled: a key_down already on its way to the helper only shows up once the interrupted batch
            // lets go of the gate, and the release checks again then. The owner's pause must not wait for it.
            _ = Task.Run(() => ReleaseHeldInputAsync(seat, state), CancellationToken.None);
        }

        audit.Add(new AgentAuditEntry(
            _time.GetUtcNow(),
            seat.Id,
            paused ? "pause" : "resume",
            paused ? "agent paused by owner" : "agent resumed by owner",
            true,
            null,
            0));
        var session = await FindSessionAsync(seat, cancellationToken).ConfigureAwait(false);
        return BuildStatus(seat, state, session, state.Helper, refusal: null, share.RootFor(seat));
    }

    public async Task<AgentBatchResult> ExecuteBatchAsync(
        SeatDefinition seat,
        AgentBatchRequest batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        EnsureEnabled(seat);
        foreach (var step in batch.Steps)
        {
            var problem = AgentRequestValidator.Validate(step.Request);
            if (problem is not null)
            {
                throw new AgentControlException(
                    AgentControlException.InvalidRequest,
                    $"Action {step.SourceIndex}: {problem}");
            }
        }

        var state = StateFor(seat);
        var epoch = state.Epoch;
        ThrowIfInterrupted(state, epoch);
        await AcquireGateAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfInterrupted(state, epoch);
            var results = new List<AgentActionResult>(batch.Steps.Count);
            int? failedIndex = null;
            string? error = null;
            string? errorCode = null;
            foreach (var step in batch.Steps)
            {
                var started = Stopwatch.GetTimestamp();
                AgentResponse response;
                try
                {
                    ThrowIfInterrupted(state, epoch);
                    // The owner may have switched the seat off or the administrator removed it from the allow
                    // list since the batch began; every action re-checks instead of trusting the snapshot.
                    await RevalidateAsync(seat, cancellationToken).ConfigureAwait(false);
                    response = step.Request.Action == AgentActions.Wait
                        ? await WaitAsync(step.Request, cancellationToken).ConfigureAwait(false)
                        : await SendLockedAsync(seat, state, step.Request, countsAsAction: true, epoch, cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (AgentControlException exception)
                {
                    response = AgentResponse.Failure(exception.Code, exception.Message);
                }

                var message = AgentAuditSummary.Clip(response.Error, MaxErrorCharacters);
                Record(seat, step.Request, response.Ok, message, started);
                results.Add(new AgentActionResult(
                    step.SourceIndex,
                    step.Request.Action,
                    response.Ok,
                    response.Ok ? null : message,
                    response.ErrorCode,
                    response.Data));
                if (!response.Ok)
                {
                    failedIndex = step.SourceIndex;
                    error = message;
                    errorCode = response.ErrorCode;
                    break;
                }

                if (step.Request.Action != AgentActions.Wait && batch.SettleMilliseconds > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(batch.SettleMilliseconds), _time, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            AgentScreenshot? screenshot = null;
            string? screenshotError = null;
            string? screenshotCode = null;
            // Once the owner has paused or stopped the agent it gets no more of the screen either.
            if (batch.Screenshot && !state.Paused && state.Epoch == epoch)
            {
                try
                {
                    var response = await SendLockedAsync(
                            seat,
                            state,
                            batch.ScreenshotOptions,
                            countsAsAction: false,
                            epoch,
                            cancellationToken)
                        .ConfigureAwait(false);
                    screenshot = response.Ok ? ReadScreenshot(response.Data, batch.ScreenshotOptions) : null;
                    if (screenshot is null)
                    {
                        screenshotError = AgentAuditSummary.Clip(
                            response.Error ?? "The helper returned no usable screenshot.",
                            MaxErrorCharacters);
                        screenshotCode = response.ErrorCode ?? AgentControlException.HelperFailed;
                    }
                }
                catch (AgentControlException exception)
                {
                    screenshotError = AgentAuditSummary.Clip(exception.Message, MaxErrorCharacters);
                    screenshotCode = exception.Code;
                }
            }

            // Actions that all ran are a success even when only the screenshot failed: a caller that retries
            // on failure would otherwise replay clicks and typing. A request that was only a screenshot has
            // nothing else to succeed at.
            var ok = failedIndex is null && (screenshotError is null || results.Count > 0);
            return new AgentBatchResult(
                ok,
                results.Count(result => result.Ok),
                failedIndex,
                ok ? null : error ?? screenshotError,
                ok ? null : errorCode ?? screenshotCode,
                results,
                screenshot,
                screenshotError);
        }
        finally
        {
            _ = state.Gate.Release();
        }
    }

    public IReadOnlyList<AgentAuditEntry> GetLog(SeatDefinition seat, int count) =>
        audit.Recent(seat.Id, count);

    public async Task<AgentCloseResult> CloseAsync(
        SeatDefinition seat,
        bool wipeFiles,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled(seat);
        var state = StateFor(seat);
        ThrowIfPaused(state);
        var started = Stopwatch.GetTimestamp();
        // Closing while a batch runs would cut it off mid-action (and one AI's close must not wreck another's
        // work), so wait briefly for the seat to be idle and otherwise refuse.
        await AcquireGateAsync(state, _options.CloseGateTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfPaused(state);
            var session = await FindSessionAsync(seat, cancellationToken).ConfigureAwait(false);
            int? sessionId = null;
            if (session is not null)
            {
                RequireUsable(session); // never the physical console
                sessionId = session.SessionId;
                if (state.Helper is { } known)
                {
                    _ = host.Stop(known);
                }

                _ = host.StopInSession(session.SessionId);
                state.Helper = null;
            }

            // Stopping the anchor first also disables its restart-on-failure, so the session stays closed.
            _ = await anchors.StopAsync(seat, cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                await sessions.LogoffAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
                await WaitForSessionToEndAsync(seat, cancellationToken).ConfigureAwait(false);
            }

            var cleared = wipeFiles ? ClearShare(seat, throwOnProblem: false) : new AgentShareClearResult(0, 0);
            var detail = session is null
                ? "The seat had no open session."
                : $"Session {session.SessionId} was logged off.";
            audit.Add(new AgentAuditEntry(
                _time.GetUtcNow(),
                seat.Id,
                "close",
                wipeFiles ? $"close seat, wiped share ({cleared.Removed} removed)" : "close seat, kept files",
                true,
                null,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return new AgentCloseResult(true, sessionId, cleared.Removed, cleared.Failed, detail);
        }
        finally
        {
            _ = state.Gate.Release();
        }
    }

    public async Task<AgentShareClearResult> CleanFilesAsync(
        SeatDefinition seat,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled(seat);
        var state = StateFor(seat);
        ThrowIfPaused(state);
        var started = Stopwatch.GetTimestamp();
        // Files the seat is working on must not vanish under a running batch.
        await AcquireGateAsync(state, _options.CloseGateTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfPaused(state);
            var cleared = ClearShare(seat, throwOnProblem: true);
            audit.Add(new AgentAuditEntry(
                _time.GetUtcNow(),
                seat.Id,
                "files_clean",
                $"wipe share ({cleared.Removed} removed, {cleared.Failed} failed)",
                cleared.Failed == 0,
                cleared.Failed == 0 ? null : $"{cleared.Failed} item(s) could not be removed",
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return cleared;
        }
        finally
        {
            _ = state.Gate.Release();
        }
    }

    /// <summary>
    /// Wipes the share. A share that cannot be wiped safely (its folder was swapped for a link) is a failure for an
    /// explicit clean, but must not undo a close that already logged the session off.
    /// </summary>
    private AgentShareClearResult ClearShare(SeatDefinition seat, bool throwOnProblem)
    {
        try
        {
            return share.Clear(seat);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
                                              or UnauthorizedAccessException)
        {
            if (throwOnProblem)
            {
                throw new AgentControlException(
                    AgentControlException.ShareUnavailable,
                    $"The file share cannot be cleaned: {AgentAuditSummary.Clip(exception.Message, MaxErrorCharacters)}",
                    exception);
            }

            return new AgentShareClearResult(0, 1);
        }
    }

    private async Task WaitForSessionToEndAsync(SeatDefinition seat, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + _options.LogoffTimeout;
        while (await FindSessionAsync(seat, cancellationToken).ConfigureAwait(false) is not null)
        {
            if (_time.GetUtcNow() >= deadline)
            {
                throw new AgentControlException(
                    AgentControlException.Timeout,
                    $"Windows did not finish logging the seat session off within {_options.LogoffTimeout.TotalSeconds:0} seconds.");
            }

            await Task.Delay(_options.SessionPollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgentResponse> WaitAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(request.Milliseconds!.Value), _time, cancellationToken)
            .ConfigureAwait(false);
        return AgentResponse.Success();
    }

    /// <summary>Sends one command to the seat's helper; the caller must hold the seat's gate.</summary>
    private async Task<AgentResponse> SendLockedAsync(
        SeatDefinition seat,
        SeatState state,
        AgentRequest request,
        bool countsAsAction,
        int epoch,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = await FindSessionAsync(seat, cancellationToken).ConfigureAwait(false) ??
                          throw new AgentControlException(
                              AgentControlException.SessionUnavailable,
                              "The seat has no active Windows session. Call start first.");
            RequireUsable(session);
            // Checked again right before (re)launching and sending: a pause or kill switch that landed while this
            // step was on its way must neither bring a helper back nor let the command through.
            ThrowIfInterrupted(state, epoch);
            var helper = await EnsureHelperAsync(seat, state, session, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.CommandTimeout);
            if (AgentActions.CanLeaveInputHeld(request.Action))
            {
                // Set before sending: if the answer is lost, pause and stop must still assume something is held.
                state.InputsMayBeHeld = true;
            }

            try
            {
                var response = await host.SendAsync(seat, helper, request, timeout.Token).ConfigureAwait(false);
                if (request.Action != AgentActions.Screenshot && response.Data is { } data &&
                    data.ToJsonString().Length > MaxResultCharacters)
                {
                    throw new AgentControlException(
                        AgentControlException.HelperFailed,
                        "The helper returned an unreasonably large result.");
                }

                if (countsAsAction)
                {
                    state.LastActionUtc = _time.GetUtcNow();
                }

                RememberDesktop(state, response);
                RememberHeld(state, response);
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AgentControlException(
                    AgentControlException.Timeout,
                    $"The helper did not answer '{request.Action}' within {_options.CommandTimeout.TotalSeconds:0} seconds.");
            }
            catch (AgentControlException exception) when (
                exception.Code == AgentControlException.HelperUnavailable && attempt == 0)
            {
                // Nothing reached the helper, so a fresh helper can safely take over the command.
                state.Helper = null;
            }
            catch (AgentControlException exception) when (
                exception.Code is AgentControlException.HelperUnavailable or AgentControlException.HelperFailed)
            {
                state.Helper = null;
                throw;
            }
        }
    }

    private Task AcquireGateAsync(SeatState state, CancellationToken cancellationToken) =>
        AcquireGateAsync(state, _options.QueueTimeout, cancellationToken);

    private static async Task AcquireGateAsync(SeatState state, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await state.Gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            throw new AgentControlException(
                AgentControlException.Busy,
                "Another agent command is still running for this seat.");
        }
    }

    private async Task<AgentHelperInfo> EnsureHelperAsync(
        SeatDefinition seat,
        SeatState state,
        SeatSession session,
        CancellationToken cancellationToken)
    {
        if (state.Helper is { } cached && cached.SessionId == session.SessionId)
        {
            return cached;
        }

        var helper = await host.TryAttachAsync(seat, session.SessionId, cancellationToken).ConfigureAwait(false);
        if (helper is null)
        {
            helper = await host.LaunchAsync(seat, session.SessionId, cancellationToken).ConfigureAwait(false);
            state.InputsMayBeHeld = false; // a new helper releases everything when it starts
        }

        state.Helper = helper;
        return helper;
    }

    /// <summary>
    /// Lets go of what key_down / mouse_down left held when the owner pauses. It waits for the interrupted batch to
    /// give up the gate, asks the live helper to release (finding it again if the service lost track of it), falls
    /// back to the one-shot release helper when the helper is gone, never talks to a session the policy refuses,
    /// and never fails the pause itself.
    /// </summary>
    private async Task ReleaseHeldInputAsync(SeatDefinition seat, SeatState state)
    {
        var started = Stopwatch.GetTimestamp();
        string? error;
        try
        {
            await AcquireGateAsync(state, CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!state.InputsMayBeHeld)
                {
                    return;
                }

                var session = await FindSessionAsync(seat, CancellationToken.None).ConfigureAwait(false);
                if (session is null || policy.RefusalForSession(session) is not null)
                {
                    return; // a session that is gone has nothing held
                }

                var helper = state.Helper is { } known && known.SessionId == session.SessionId && host.IsRunning(known)
                    ? known
                    : await host.TryAttachAsync(seat, session.SessionId, CancellationToken.None).ConfigureAwait(false);
                if (helper is null)
                {
                    // The helper that held the input crashed or was lost; a one-shot helper lets go of what it left.
                    var launched = host.ReleaseInputs(seat, session.SessionId);
                    state.InputsMayBeHeld = !launched;
                    error = launched ? null : "The one-shot release helper could not be started.";
                }
                else
                {
                    state.Helper = helper;
                    using var timeout = new CancellationTokenSource(_options.CommandTimeout);
                    var response = await host.SendAsync(seat, helper, new AgentRequest { Action = AgentActions.Release }, timeout.Token)
                        .ConfigureAwait(false);
                    RememberHeld(state, response);
                    error = !response.Ok
                        ? AgentAuditSummary.Clip(response.Error, MaxErrorCharacters)
                        : state.InputsMayBeHeld
                            ? "Windows refused to release some held input (locked screen or secure prompt?); the helper keeps retrying."
                            : null;
                }
            }
            finally
            {
                _ = state.Gate.Release();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = AgentAuditSummary.Clip(exception.Message, MaxErrorCharacters);
        }

        audit.Add(new AgentAuditEntry(
            _time.GetUtcNow(),
            seat.Id,
            AgentActions.Release,
            "release held keys and buttons (owner paused)",
            error is null,
            error,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }

    private async Task<SeatSession> StartSeatSessionAsync(SeatDefinition seat, CancellationToken cancellationToken)
    {
        try
        {
            await anchors.StartAsync(seat, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AgentControlException(
                AgentControlException.SessionUnavailable,
                $"The seat's hidden RDP anchor could not be started: {exception.Message}",
                exception);
        }

        var deadline = _time.GetUtcNow() + _options.SessionStartTimeout;
        while (true)
        {
            var session = await FindSessionAsync(seat, cancellationToken).ConfigureAwait(false);
            if (session is not null)
            {
                return session;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                throw new AgentControlException(
                    AgentControlException.SessionUnavailable,
                    $"The seat session did not appear within {_options.SessionStartTimeout.TotalSeconds:0} seconds.");
            }

            await Task.Delay(_options.SessionPollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SeatSession?> FindSessionAsync(SeatDefinition seat, CancellationToken cancellationToken)
    {
        var views = await manager.GetViewsAsync(cancellationToken).ConfigureAwait(false);
        return views
            .Where(view => string.Equals(view.Seat.Id, seat.Id, StringComparison.OrdinalIgnoreCase))
            .SelectMany(view => view.Sessions)
            .Where(session => session.State is
                SeatSessionState.Active or SeatSessionState.Connected or SeatSessionState.Shadow)
            .OrderByDescending(session => session.State == SeatSessionState.Active)
            .ThenByDescending(session => session.SessionId)
            .FirstOrDefault();
    }

    private string? SeatRefusal(SeatDefinition seat) =>
        !seat.Enabled || !seat.AgentControlEnabled
            ? $"Agent control is not enabled for seat '{seat.Id}'."
            : policy.Refusal(seat);

    private void EnsureEnabled(SeatDefinition seat)
    {
        if (SeatRefusal(seat) is { } refusal)
        {
            throw new AgentControlException(AgentControlException.Disabled, refusal);
        }
    }

    private void RequireUsable(SeatSession session)
    {
        if (policy.RefusalForSession(session) is { } refusal)
        {
            throw new AgentControlException(AgentControlException.Disabled, refusal);
        }
    }

    /// <summary>Re-reads the seat so a switch-off or allow-list change takes effect inside a running batch.</summary>
    private async Task RevalidateAsync(SeatDefinition seat, CancellationToken cancellationToken)
    {
        var current = await manager.GetAsync(seat.Id, cancellationToken).ConfigureAwait(false) ??
                      throw new AgentControlException(
                          AgentControlException.Disabled,
                          $"Seat '{seat.Id}' no longer exists.");
        EnsureEnabled(current);
    }

    private static void ThrowIfPaused(SeatState state)
    {
        if (state.Paused)
        {
            throw new AgentControlException(
                AgentControlException.Paused,
                "Agent control is paused by the owner. Resume it from AgentSeat to continue.");
        }
    }

    /// <summary>Raises when the owner paused or hit the kill switch after this batch was submitted.</summary>
    private static void ThrowIfInterrupted(SeatState state, int epoch)
    {
        ThrowIfPaused(state);
        if (state.Epoch != epoch)
        {
            throw new AgentControlException(
                AgentControlException.Stopped,
                "Agent control was stopped by the owner while this batch was waiting or running.");
        }
    }

    private SeatState StateFor(SeatDefinition seat) => _states.GetOrAdd(seat.Id, _ => new SeatState());

    private static void RememberDesktop(SeatState state, AgentResponse response)
    {
        if (response.Data is not { } data)
        {
            return;
        }

        var width = ReadInt(data, "desktopWidth");
        var height = ReadInt(data, "desktopHeight");
        if (width > 0 && height > 0)
        {
            state.DesktopWidth = width;
            state.DesktopHeight = height;
        }
    }

    /// <summary>Commands that hold or release input report what is still held; anything else leaves it unknown.</summary>
    private static void RememberHeld(SeatState state, AgentResponse response)
    {
        if (response.Data?["held"] is JsonArray held)
        {
            state.InputsMayBeHeld = held.Count > 0;
        }
    }

    private void Record(SeatDefinition seat, AgentRequest request, bool ok, string? error, long startedTimestamp) =>
        audit.Add(new AgentAuditEntry(
            _time.GetUtcNow(),
            seat.Id,
            request.Action,
            AgentAuditSummary.Describe(request),
            ok,
            ok ? null : error,
            (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds));

    private static AgentStatus BuildStatus(
        SeatDefinition seat,
        SeatState state,
        SeatSession? session,
        AgentHelperInfo? helper,
        string? refusal,
        string? sharePath)
    {
        var detail = refusal is not null
            ? refusal
            : !seat.AgentControlEnabled
                ? "Agent control is disabled for this seat."
                : state.Paused
                    ? "Paused by the owner."
                    : session is null
                        ? "The seat has no active Windows session; call start."
                        : helper is null
                            ? "Session is up; the helper starts on the first command."
                            : "Ready.";
        return new AgentStatus(
            seat.Id,
            seat.AgentControlEnabled,
            session is not null,
            session?.SessionId,
            helper is not null,
            helper?.ProcessId,
            state.Paused,
            state.DesktopWidth,
            state.DesktopHeight,
            state.LastActionUtc,
            detail,
            sharePath,
            Refused: refusal is not null);
    }

    private static AgentScreenshot? ReadScreenshot(JsonObject? data, AgentRequest request)
    {
        if (data is null || ReadImage(data) is not { } image)
        {
            return null;
        }

        var (mime, base64) = image;
        // A change report only makes sense against the frame the caller named, so anything else is ignored.
        var signature = ReadString(data, "signature") is { } text && AgentFrameSignature.TryDecode(text, out _) ? text : null;
        var compared = signature is not null && request.Since is not null;
        return new AgentScreenshot(
            mime,
            ReadInt(data, "width"),
            ReadInt(data, "height"),
            base64,
            request.Region is null
                ? null
                : new AgentRegion(ReadInt(data, "regionX"), ReadInt(data, "regionY"), ReadInt(data, "regionWidth"), ReadInt(data, "regionHeight")),
            signature,
            compared && data["changed"] is JsonValue changed && changed.TryGetValue<bool>(out var flag) ? flag : null,
            compared ? ReadChanges(data["changes"]) : null);
    }

    /// <summary>
    /// The helper runs as an untrusted seat user: accept only the two image types it should produce, and only
    /// data that really is base64.
    /// </summary>
    private static (string Mime, string Base64)? ReadImage(JsonObject data)
    {
        var base64 = ReadString(data, "dataBase64");
        var mime = ReadString(data, "mimeType");
        return string.IsNullOrEmpty(base64) || mime is not ("image/png" or "image/jpeg") || !IsBase64(base64)
            ? null
            : (mime, base64);
    }

    private static IReadOnlyList<AgentScreenshotChange>? ReadChanges(JsonNode? node)
    {
        if (node is not JsonArray entries || entries.Count > MaxChangeRegions)
        {
            return null;
        }

        var changes = new List<AgentScreenshotChange>(entries.Count);
        foreach (var item in entries)
        {
            if (item is not JsonObject entry || ReadInt(entry, "width") < 1 || ReadInt(entry, "height") < 1)
            {
                return null;
            }

            var crop = ReadImage(entry);
            changes.Add(new AgentScreenshotChange(
                ReadInt(entry, "x"),
                ReadInt(entry, "y"),
                ReadInt(entry, "width"),
                ReadInt(entry, "height"),
                crop?.Mime,
                crop?.Base64));
        }

        return changes;
    }

    private static string? ReadString(JsonObject data, string name) =>
        data[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsBase64(string value)
    {
        var buffer = new byte[(value.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(value, buffer, out _);
    }

    private static int ReadInt(JsonObject data, string name) =>
        data[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private sealed class SeatState
    {
        private int _epoch;

        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal volatile bool Paused;

        /// <summary>Whether key_down / mouse_down may have left something held in the session.</summary>
        internal volatile bool InputsMayBeHeld;

        internal AgentHelperInfo? Helper { get; set; }

        internal DateTimeOffset? LastActionUtc { get; set; }

        internal int? DesktopWidth { get; set; }

        internal int? DesktopHeight { get; set; }

        /// <summary>Changes whenever the owner pauses or stops, invalidating every batch that began earlier.</summary>
        internal int Epoch => Volatile.Read(ref _epoch);

        internal void Interrupt() => Interlocked.Increment(ref _epoch);
    }
}
