using AgentSeat.Core.Models;

namespace AgentSeat.Core.Abstractions;

public interface ISeatStore
{
    Task<IReadOnlyList<SeatDefinition>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<SeatDefinition?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task UpsertAsync(SeatDefinition seat, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
