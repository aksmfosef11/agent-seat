using AgentSeat.Core.Models;

namespace AgentSeat.Core.Abstractions;

public interface IWindowsSessionService
{
    Task<IReadOnlyList<SeatSession>> GetSessionsAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(int sessionId, CancellationToken cancellationToken = default);

    Task LogoffAsync(int sessionId, CancellationToken cancellationToken = default);
}
