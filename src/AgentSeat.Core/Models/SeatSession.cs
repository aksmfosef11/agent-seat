namespace AgentSeat.Core.Models;

public enum SeatSessionState
{
    Unknown,
    Active,
    Connected,
    ConnectQuery,
    Shadow,
    Disconnected,
    Idle,
    Listening,
    Reset,
    Down,
    Initializing
}

public sealed record SeatSession(
    int SessionId,
    string UserName,
    string DomainName,
    string ClientName,
    string StationName,
    SeatSessionState State);

public sealed record SeatView(SeatDefinition Seat, IReadOnlyList<SeatSession> Sessions)
{
    public bool Online => Sessions.Any(session => session.State is
        SeatSessionState.Active or SeatSessionState.Connected or SeatSessionState.Shadow);
}
