using AgentSeat.Core.Models;
using AgentSeat.Core.Services;

namespace AgentSeat.Core.Tests;

public sealed class SunshineConfigurationBuilderTests
{
    [Fact]
    public void Build_EmitsIsolatedSecureSeatConfiguration()
    {
        var seat = new SeatDefinition
        {
            Id = "friend",
            DisplayName = "Friend",
            UserName = "seat-friend",
            SunshineBasePort = 48089
        };
        var root = Path.Combine(Path.GetTempPath(), "AgentSeat Config", "friend");

        var config = SunshineConfigurationBuilder.Build(
            seat,
            new SunshineInstancePaths(
                Path.Combine(root, "apps.json"),
                Path.Combine(root, "state.json"),
                Path.Combine(root, "sunshine.log"),
                Path.Combine(root, "cakey.pem"),
                Path.Combine(root, "cacert.pem")));

        Assert.Contains("port = 48089", config, StringComparison.Ordinal);
        Assert.Contains("sunshine_name = AgentSeat - Friend", config, StringComparison.Ordinal);
        Assert.Contains("origin_web_ui_allowed = pc", config, StringComparison.Ordinal);
        Assert.Contains("upnp = disabled", config, StringComparison.Ordinal);
        Assert.Contains("dd_configuration_option = disabled", config, StringComparison.Ordinal);
        Assert.Contains("file_state = ", config, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', config);
    }
}
