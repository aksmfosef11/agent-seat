using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;

namespace AgentSeat.Service;

// Keeps the seat management contract without launching or stopping any game streaming process.
internal sealed class DisabledStreamingService : ISeatStreamingService
{
    private static SunshineRuntimeStatus Status => new(SunshineRuntimeState.Disabled, null, "", null, null,
        false, false, null, "Game streaming is excluded from agent-seat. Use the seat viewer.");
    public Task<SunshineRuntimeStatus> GetStatusAsync(SeatDefinition seat, CancellationToken cancellationToken = default) => Task.FromResult(Status);
    public Task<SunshineRuntimeStatus> StartAsync(SeatDefinition seat, SeatSession session, CancellationToken cancellationToken = default) => Task.FromResult(Status);
    public Task<SunshineRuntimeStatus> StopAsync(SeatDefinition seat, CancellationToken cancellationToken = default) => Task.FromResult(Status);
    public Task<SunshinePairResult> PairAsync(SeatDefinition seat, string pin, string clientName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SunshinePairResult(false, Status.Detail));
}
