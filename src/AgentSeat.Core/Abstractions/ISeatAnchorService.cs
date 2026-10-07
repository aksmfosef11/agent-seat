using AgentSeat.Core.Models;

namespace AgentSeat.Core.Abstractions;

public interface ISeatAnchorService
{
    Task StartAsync(SeatDefinition seat, CancellationToken cancellationToken = default);

    Task<bool> StopAsync(SeatDefinition seat, CancellationToken cancellationToken = default);
}
