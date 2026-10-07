using System.Text.Json.Nodes;
using AgentSeat.Core.Services;

namespace AgentSeat.Core.Tests;

public sealed class SunshineApplicationsBuilderTests
{
    [Fact]
    public void BuildDefaultCatalog_EmitsCredentialFreeSteamEntry()
    {
        var content = SunshineApplicationsBuilder.BuildDefaultCatalog(
            "friend-seat",
            @"C:\Program Files (x86)\Steam\steam.exe",
            @"C:\Program Files\agent-seat\app-launcher\AgentSeat.SteamLauncher.exe",
            @"C:\Program Files\agent-seat\compat");
        var root = JsonNode.Parse(content)!.AsObject();
        var apps = root["apps"]!.AsArray();
        var steam = Assert.Single(apps)!.AsObject();

        Assert.Empty(root["env"]!.AsObject());
        Assert.Equal("Steam", steam["name"]!.GetValue<string>());
        Assert.Equal(
            "\"C:\\Program Files\\agent-seat\\app-launcher\\AgentSeat.SteamLauncher.exe\" --seat friend-seat --source \"C:\\Program Files (x86)\\Steam\\steam.exe\" --compat \"C:\\Program Files\\agent-seat\\compat\"",
            steam["cmd"]!.GetValue<string>());
        Assert.True(steam["auto-detach"]!.GetValue<bool>());
        Assert.True(steam["wait-all"]!.GetValue<bool>());
        Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsUnmodifiedUpstreamDefault_ProtectsCustomizedCatalogs()
    {
        const string upstreamDefault = """
            {
              "env": {},
              "apps": [
                {
                  "name": "Desktop",
                  "image-path": "desktop.png"
                },
                {
                  "name": "Steam Big Picture",
                  "cmd": "steam://open/bigpicture",
                  "prep-cmd": [
                    {
                      "do": "",
                      "undo": "steam://close/bigpicture"
                    }
                  ],
                  "auto-detach": true,
                  "wait-all": true,
                  "image-path": "steam.png"
                }
              ]
            }
            """;
        var customized = upstreamDefault.Replace(
            "\"apps\": [",
            "\"apps\": [{ \"name\": \"My Game\", \"cmd\": \"game.exe\" },",
            StringComparison.Ordinal);

        Assert.True(SunshineApplicationsBuilder.IsUnmodifiedUpstreamDefault(upstreamDefault));
        Assert.False(SunshineApplicationsBuilder.IsUnmodifiedUpstreamDefault(customized));
        Assert.False(SunshineApplicationsBuilder.IsUnmodifiedUpstreamDefault("not json"));
    }

    [Fact]
    public void IsUnmodifiedAgentSeatDefault_RecognizesManagedSteamCatalogs()
    {
        var managed = SunshineApplicationsBuilder.BuildDefaultCatalog(
            "friend-seat",
            @"D:\Steam\steam.exe",
            @"C:\agent-seat\AgentSeat.SteamLauncher.exe",
            @"C:\agent-seat\compat");
        var legacyUriRoot = JsonNode.Parse(managed)!.AsObject();
        legacyUriRoot["apps"]!.AsArray()[0]!["cmd"] = "steam://open/bigpicture";
        var legacyUri = legacyUriRoot.ToJsonString();
        var legacyDirectRoot = JsonNode.Parse(managed)!.AsObject();
        legacyDirectRoot["apps"]!.AsArray()[0]!["cmd"] =
            "\"D:\\Steam\\steam.exe\" -master_ipc_name_override AgentSeat_friend-seat -bigpicture";
        var legacyDirect = legacyDirectRoot.ToJsonString();
        var legacyDesktopRoot = JsonNode.Parse(managed)!.AsObject();
        legacyDesktopRoot["apps"]!.AsArray().Insert(0, new JsonObject
        {
            ["name"] = "Desktop",
            ["image-path"] = "desktop.png"
        });
        var legacyDesktop = legacyDesktopRoot.ToJsonString();
        var customized = managed.Replace(
            "\"Steam\"",
            "\"My Steam\"",
            StringComparison.Ordinal);

        Assert.True(SunshineApplicationsBuilder.IsUnmodifiedAgentSeatDefault(managed, "friend-seat"));
        Assert.True(SunshineApplicationsBuilder.IsUnmodifiedAgentSeatDefault(legacyUri, "friend-seat"));
        Assert.True(SunshineApplicationsBuilder.IsUnmodifiedAgentSeatDefault(legacyDirect, "friend-seat"));
        Assert.True(SunshineApplicationsBuilder.IsUnmodifiedAgentSeatDefault(legacyDesktop, "friend-seat"));
        Assert.False(SunshineApplicationsBuilder.IsUnmodifiedAgentSeatDefault(managed, "other-seat"));
        Assert.False(SunshineApplicationsBuilder.IsUnmodifiedAgentSeatDefault(customized, "friend-seat"));
    }
}
