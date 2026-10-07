using AgentSeat.Core.Agent;
using AgentSeat.Core.Models;

namespace AgentSeat.Core.Abstractions;

/// <summary>Lets an AI agent see and drive one seat's Windows session without touching the host console.</summary>
public interface IAgentControlService
{
    Task<AgentStatus> GetStatusAsync(SeatDefinition seat, CancellationToken cancellationToken = default);

    /// <summary>Brings the seat session up if needed and makes sure its helper answers.</summary>
    Task<AgentStatus> StartAsync(SeatDefinition seat, CancellationToken cancellationToken = default);

    /// <summary>Kill switch: terminates the session helper. The Windows session itself keeps running.</summary>
    Task<AgentStatus> StopAsync(SeatDefinition seat, CancellationToken cancellationToken = default);

    Task<AgentStatus> SetPausedAsync(SeatDefinition seat, bool paused, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the actions in order while holding the seat's command gate (so nothing interleaves),
    /// then captures one screenshot. Throws <see cref="AgentControlException"/> only when nothing
    /// ran (disabled, invalid, paused, busy); failures after that are reported in the result.
    /// </summary>
    Task<AgentBatchResult> ExecuteBatchAsync(
        SeatDefinition seat,
        AgentBatchRequest batch,
        CancellationToken cancellationToken = default);

    IReadOnlyList<AgentAuditEntry> GetLog(SeatDefinition seat, int count);

    /// <summary>
    /// Ends the seat's session so it stops using resources and can be reopened clean: stops the helper and
    /// the hidden RDP anchor, logs the seat's session off, and (by default) wipes the seat's file share.
    /// Refused while a batch is running, while paused, and for the physical console session.
    /// </summary>
    Task<AgentCloseResult> CloseAsync(SeatDefinition seat, bool wipeFiles, CancellationToken cancellationToken = default);

    /// <summary>Deletes everything in the seat's file share (never anything outside it).</summary>
    Task<AgentShareClearResult> CleanFilesAsync(SeatDefinition seat, CancellationToken cancellationToken = default);
}

/// <summary>
/// A per-seat folder both the owner and the seat account can read and write, used to hand files to and from
/// the agent seat. Its location is fixed by configuration, never chosen by a caller.
/// </summary>
public interface IAgentFileShare
{
    string RootFor(SeatDefinition seat);

    /// <summary>
    /// Removes the folder's contents (not the folder). Links the seat account planted inside it are removed as
    /// links and never followed, so nothing outside the folder can be deleted.
    /// </summary>
    AgentShareClearResult Clear(SeatDefinition seat);
}

/// <summary>
/// The Windows-specific half of agent control: locating, launching, talking to and killing the
/// helper process that lives inside a seat's WTS session.
/// </summary>
public interface IAgentHelperHost
{
    /// <summary>Finds a helper already running in the session and verifies it answers; null if none.</summary>
    Task<AgentHelperInfo?> TryAttachAsync(SeatDefinition seat, int sessionId, CancellationToken cancellationToken);

    Task<AgentHelperInfo> LaunchAsync(SeatDefinition seat, int sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends one command. Throws <see cref="AgentControlException"/> with
    /// <see cref="AgentControlException.HelperUnavailable"/> when the helper could not be reached
    /// at all (safe to retry), or <see cref="AgentControlException.HelperFailed"/> when it was
    /// reached but the exchange broke (not safe to retry blindly).
    /// </summary>
    Task<AgentResponse> SendAsync(
        SeatDefinition seat,
        AgentHelperInfo helper,
        AgentRequest request,
        CancellationToken cancellationToken);

    bool IsRunning(AgentHelperInfo helper);

    bool Stop(AgentHelperInfo helper);

    /// <summary>
    /// Kills every verified helper process in the session without talking to it, so the kill switch
    /// works even when the helper is frozen. Returns how many were stopped.
    /// </summary>
    int StopInSession(int sessionId);

    /// <summary>
    /// Starts a short-lived helper in the session that only lets go of held keys and mouse buttons and exits,
    /// without opening a pipe. The kill switch uses it after killing a helper that may have been holding input.
    /// Returns whether it could be started.
    /// </summary>
    bool ReleaseInputs(SeatDefinition seat, int sessionId);
}

/// <summary>
/// The administrator's decision about which seats an agent may drive. It must not be changeable through
/// the management API, because the agent holds the agent token and can reach that API: otherwise it could
/// register the owner's own account as a "seat" and control the owner's desktop.
/// </summary>
public interface IAgentSeatPolicy
{
    /// <summary>Why this seat may not be agent-controlled, or <see langword="null"/> when it may.</summary>
    string? Refusal(SeatDefinition seat);

    /// <summary>Why this session may not be agent-controlled (for example the physical console), or null.</summary>
    string? RefusalForSession(SeatSession session);
}
