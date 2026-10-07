namespace AgentSeat.Core.Models;

/// <summary>
/// The complete port family derived by Sunshine from its configured base port.
/// </summary>
public sealed record SunshinePorts(
    int Base,
    int GameStreamHttps,
    int GameStreamHttp,
    int WebUiHttps,
    int Video,
    int Control,
    int Audio,
    int Rtsp)
{
    public const int MinimumBasePort = 1029;
    public const int MaximumBasePort = 65514;

    public IReadOnlyList<int> TcpPorts =>
        [GameStreamHttps, GameStreamHttp, Rtsp];

    public IReadOnlyList<int> UdpPorts =>
        [Video, Control, Audio];

    public IReadOnlyList<int> LocalOnlyPorts =>
        [WebUiHttps];

    public IReadOnlyList<int> AllPorts =>
        [GameStreamHttps, GameStreamHttp, WebUiHttps, Video, Control, Audio, Rtsp];

    public static SunshinePorts FromBasePort(int basePort)
    {
        if (basePort is < MinimumBasePort or > MaximumBasePort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(basePort),
                $"Sunshine base port must be between {MinimumBasePort} and {MaximumBasePort}.");
        }

        return new SunshinePorts(
            basePort,
            basePort - 5,
            basePort,
            basePort + 1,
            basePort + 9,
            basePort + 10,
            basePort + 11,
            basePort + 21);
    }
}
