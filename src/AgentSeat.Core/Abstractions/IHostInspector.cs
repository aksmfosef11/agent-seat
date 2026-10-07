using AgentSeat.Core.Models;

namespace AgentSeat.Core.Abstractions;

public interface IHostInspector
{
    Task<PreflightReport> InspectAsync(
        IReadOnlyCollection<SeatDefinition> seats,
        CancellationToken cancellationToken = default);
}
