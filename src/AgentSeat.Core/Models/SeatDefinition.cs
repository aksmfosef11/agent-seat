namespace AgentSeat.Core.Models;

public sealed record SeatDefinition
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public string HostAddress { get; init; } = Environment.MachineName;

    public int RdpPort { get; init; } = 3389;

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public bool FullScreen { get; init; } = true;

    public bool PlayAudioOnClient { get; init; } = true;

    public bool RedirectClipboard { get; init; }

    public bool StreamingEnabled { get; init; } = true;

    /// <summary>
    /// Starts the seat's Sunshine instance as soon as its hidden RDP anchor creates an active
    /// Windows session. A manual stop remains stopped until that session changes or the service
    /// restarts.
    /// </summary>
    public bool AutoStartStreaming { get; init; } = true;

    /// <summary>
    /// Sunshine's GameStream HTTP base port. A value of zero is accepted on create/update requests
    /// and replaced with the first collision-free AgentSeat port family.
    /// </summary>
    public int SunshineBasePort { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Lets a token-authenticated local AI agent watch and drive this seat's Windows session
    /// through the agent API. Off by default; turning it on is a management action and the
    /// agent token is still required for every call.
    /// </summary>
    public bool AgentControlEnabled { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
