namespace AgentSeat.Core.Services;

public sealed class SeatValidationException : Exception
{
    public SeatValidationException(string message)
        : base(message)
    {
    }
}
