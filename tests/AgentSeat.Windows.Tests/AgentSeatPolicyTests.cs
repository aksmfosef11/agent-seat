using AgentSeat.Core.Models;
using AgentSeat.Windows;

namespace AgentSeat.Windows.Tests;

/// <summary>
/// The allow list is what keeps an agent from registering the owner's own account as a "seat" through the
/// unauthenticated management API and then driving the owner's real desktop.
/// </summary>
public sealed class AgentSeatPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-seat-policy-" + Guid.NewGuid().ToString("N"));

    public AgentSeatPolicyTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string AllowlistPath => Path.Combine(_directory, "agent-seats.json");

    private AgentSeatPolicy NewPolicy(bool allowConsole = false, int console = 1) =>
        new(new AgentSeatPolicyOptions(AllowlistPath) { AllowConsoleSession = allowConsole }, () => console);

    private static SeatDefinition Seat(string id = "agent", string user = "seat-agent") => new()
    {
        Id = id,
        DisplayName = id,
        UserName = user,
        HostAddress = "pc",
        AgentControlEnabled = true
    };

    private void WriteAllowlist(string json) => File.WriteAllText(AllowlistPath, json);

    [Fact]
    public void NothingIsApprovedWithoutAnAllowList()
    {
        var refusal = NewPolicy().Refusal(Seat());

        Assert.NotNull(refusal);
        Assert.Contains("Enable-AgentControl.ps1", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ASeatIsApprovedOnlyWhenIdAndWindowsUserBothMatch()
    {
        WriteAllowlist("""{ "seats": [ { "seatId": "agent", "userName": "seat-agent" } ] }""");
        var policy = NewPolicy();

        Assert.Null(policy.Refusal(Seat()));
        Assert.Null(policy.Refusal(Seat("AGENT", @"PC\Seat-Agent"))); // id and domain/case are normalized
        Assert.Null(policy.Refusal(Seat(user: "seat-agent@pc")));
    }

    [Fact]
    public void RepointingAnApprovedSeatAtAnotherAccountIsRefused()
    {
        WriteAllowlist("""{ "seats": [ { "seatId": "agent", "userName": "seat-agent" } ] }""");

        // The management API (reachable by the agent) can rewrite a seat's user; that must not carry approval along.
        Assert.NotNull(NewPolicy().Refusal(Seat(user: "console-owner")));
        Assert.NotNull(NewPolicy().Refusal(Seat(user: @"DESKTOP\console-owner")));
    }

    [Fact]
    public void ANewSeatForTheOwnersAccountIsRefused()
    {
        WriteAllowlist("""{ "seats": [ { "seatId": "agent", "userName": "seat-agent" } ] }""");

        var refusal = NewPolicy().Refusal(Seat("sneaky", "console-owner"));

        Assert.NotNull(refusal);
        Assert.Contains("not approved", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "seats": "nope" }""")]
    [InlineData("""{ "seats": [ { "seatId": "", "userName": "x" }, { "userName": "x" }, 7 ] }""")]
    [InlineData("""[ ]""")]
    public void AMalformedAllowListApprovesNothing(string json)
    {
        WriteAllowlist(json);

        Assert.NotNull(NewPolicy().Refusal(Seat()));
    }

    [Fact]
    public void ChangesToTheAllowListAreNoticedWithoutARestart()
    {
        var policy = NewPolicy();
        Assert.NotNull(policy.Refusal(Seat()));

        WriteAllowlist("""{ "seats": [ { "seatId": "agent", "userName": "seat-agent" } ] }""");
        Assert.Null(policy.Refusal(Seat()));

        WriteAllowlist("""{ "seats": [ ] }""");
        File.SetLastWriteTimeUtc(AllowlistPath, DateTime.UtcNow.AddSeconds(5));
        Assert.NotNull(policy.Refusal(Seat()));
    }

    [Fact]
    public void ThePhysicalConsoleSessionIsRefusedByDefault()
    {
        var session = new SeatSession(1, "console-owner", "PC", "", "Console", SeatSessionState.Active);

        var refusal = NewPolicy(console: 1).RefusalForSession(session);

        Assert.NotNull(refusal);
        Assert.Contains("physical console", refusal, StringComparison.Ordinal);
        Assert.Null(NewPolicy(console: 1).RefusalForSession(session with { SessionId = 3 }));
    }

    [Fact]
    public void ConsoleRefusalCanBeOverriddenOnlyThroughConfiguration()
    {
        var session = new SeatSession(1, "console-owner", "PC", "", "Console", SeatSessionState.Active);

        Assert.Null(NewPolicy(allowConsole: true, console: 1).RefusalForSession(session));
    }
}
