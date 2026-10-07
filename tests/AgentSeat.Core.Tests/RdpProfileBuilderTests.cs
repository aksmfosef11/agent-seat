using AgentSeat.Core.Models;
using AgentSeat.Core.Services;

namespace AgentSeat.Core.Tests;

public sealed class RdpProfileBuilderTests
{
    [Fact]
    public void Build_ProducesGamingFriendlyProfileWithoutStoredPassword()
    {
        var seat = new SeatDefinition
        {
            Id = "friend",
            DisplayName = "Friend",
            UserName = "seat-friend",
            HostAddress = "100.64.1.20",
            RdpPort = 3390,
            Width = 2560,
            Height = 1440,
            FullScreen = true,
            PlayAudioOnClient = true,
            RedirectClipboard = false
        };

        var profile = RdpProfileBuilder.Build(seat);

        Assert.Contains("full address:s:100.64.1.20:3390\r\n", profile, StringComparison.Ordinal);
        Assert.Contains("username:s:.\\seat-friend\r\n", profile, StringComparison.Ordinal);
        Assert.Contains("desktopwidth:i:2560\r\n", profile, StringComparison.Ordinal);
        Assert.Contains("audiomode:i:0\r\n", profile, StringComparison.Ordinal);
        Assert.Contains("redirectclipboard:i:0\r\n", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("password", profile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_BracketsIpv6Address()
    {
        var profile = RdpProfileBuilder.Build(ValidSeat() with { HostAddress = "fd00::10" });

        Assert.Contains("full address:s:[fd00::10]:3389", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsLineInjection()
    {
        var seat = ValidSeat() with { HostAddress = "host\r\npassword 51:b:evil" };

        Assert.Throws<ArgumentException>(() => RdpProfileBuilder.Build(seat));
    }

    [Fact]
    public void BuildUnicodeFile_HasUtf16LittleEndianBom()
    {
        var bytes = RdpProfileBuilder.BuildUnicodeFile(ValidSeat());

        Assert.True(bytes.Length > 2);
        Assert.Equal(0xff, bytes[0]);
        Assert.Equal(0xfe, bytes[1]);
    }

    private static SeatDefinition ValidSeat() => new()
    {
        Id = "friend",
        DisplayName = "Friend",
        UserName = "seat-friend",
        HostAddress = "gaming-pc"
    };
}
