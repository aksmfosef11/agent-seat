namespace AgentSeat.Core.Models;

public enum SunshineRuntimeState
{
    Disabled,
    MissingTemplate,
    MissingCompatibility,
    Stopped,
    Starting,
    Ready,
    Faulted
}

public sealed record SunshineRuntimeStatus(
    SunshineRuntimeState State,
    SunshinePorts? Ports,
    string MoonlightAddress,
    int? SessionId,
    int? ProcessId,
    bool EncoderReady,
    bool GamepadReady,
    DateTimeOffset? StartedAtUtc,
    string Detail);

public sealed record SunshinePairResult(bool Accepted, string Detail);
