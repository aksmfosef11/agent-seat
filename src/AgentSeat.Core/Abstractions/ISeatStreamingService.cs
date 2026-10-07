using AgentSeat.Core.Models;

namespace AgentSeat.Core.Abstractions;

public interface ISeatStreamingService
{
    Task<SunshineRuntimeStatus> GetStatusAsync(
        SeatDefinition seat,
        CancellationToken cancellationToken = default);

    Task<SunshineRuntimeStatus> StartAsync(
        SeatDefinition seat,
        SeatSession session,
        CancellationToken cancellationToken = default);

    Task<SunshineRuntimeStatus> StopAsync(
        SeatDefinition seat,
        CancellationToken cancellationToken = default);

    Task<SunshinePairResult> PairAsync(
        SeatDefinition seat,
        string pin,
        string clientName,
        CancellationToken cancellationToken = default);
}
