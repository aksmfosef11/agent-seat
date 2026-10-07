using AgentSeat.Core.Agent;

namespace AgentSeat.Core.Tests;

public sealed class AgentViewerSessionsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void TicketIsOneUseAndCookieWorksOnlyForItsSeatAndCurrentToken()
    {
        var sessions = new AgentViewerSessions();
        var ticket = sessions.Issue("agent", "fingerprint");
        var session = sessions.Redeem(ticket, "fingerprint")!;
        Assert.Null(sessions.Redeem(ticket, "fingerprint"));
        Assert.True(sessions.Authorize(session.Cookie, "agent", "fingerprint"));
        Assert.False(sessions.Authorize(session.Cookie, "other", "fingerprint"));
        Assert.False(sessions.Authorize(session.Cookie, "agent", "rotated"));
        Assert.False(sessions.Authorize(session.Cookie, "agent", null));
    }
    [Fact]
    public void TicketExpiresAfterOneMinuteAndSessionAfterThirtyMinutes()
    {
        var clock = new Clock();
        var sessions = new AgentViewerSessions(clock);
        var expired = sessions.Issue("agent", "token");
        clock.Now = clock.Now.AddMinutes(1);
        Assert.Null(sessions.Redeem(expired, "token"));
        var valid = sessions.Redeem(sessions.Issue("agent", "token"), "token")!;
        clock.Now = clock.Now.AddMinutes(30);
        Assert.False(sessions.Authorize(valid.Cookie, "agent", "token"));
    }
    [Fact]
    public void RotationInvalidatesPendingTicketsAndMalformedCredentialsFailClosed()
    {
        var sessions = new AgentViewerSessions();
        Assert.Null(sessions.Redeem(sessions.Issue("agent", "old"), "new"));
        Assert.Null(sessions.Redeem(null, "new"));
        Assert.False(sessions.Authorize(new string('x', 100000), "agent", "new"));
    }
    [Fact]
    public void PendingTicketsAreBoundedAndExpiredEntriesAreReclaimed()
    {
        var clock = new Clock();
        var sessions = new AgentViewerSessions(clock);
        for (var index = 0; index < 128; index++) sessions.Issue("agent", "token");
        Assert.Throws<InvalidOperationException>(() => sessions.Issue("agent", "token"));
        clock.Now = clock.Now.AddMinutes(1);
        Assert.NotNull(sessions.Issue("agent", "token"));
    }
}
