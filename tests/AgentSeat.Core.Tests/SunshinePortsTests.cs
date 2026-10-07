using AgentSeat.Core.Models;

namespace AgentSeat.Core.Tests;

public sealed class SunshinePortsTests
{
    [Fact]
    public void FromBasePort_MapsEverySunshineListener()
    {
        var ports = SunshinePorts.FromBasePort(47989);

        Assert.Equal(47984, ports.GameStreamHttps);
        Assert.Equal(47989, ports.GameStreamHttp);
        Assert.Equal(47990, ports.WebUiHttps);
        Assert.Equal([47998, 47999, 48000], ports.UdpPorts);
        Assert.Equal(48010, ports.Rtsp);
        Assert.Equal(7, ports.AllPorts.Distinct().Count());
    }

    [Theory]
    [InlineData(1028)]
    [InlineData(65515)]
    public void FromBasePort_RejectsValuesThatWouldOverflowThePortFamily(int basePort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SunshinePorts.FromBasePort(basePort));
    }
}
