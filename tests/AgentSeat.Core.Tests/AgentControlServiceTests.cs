using System.Text.Json.Nodes;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Agent;
using AgentSeat.Core.Models;
using AgentSeat.Core.Services;

namespace AgentSeat.Core.Tests;

public sealed class AgentControlServiceTests
{
    private static readonly SeatDefinition AgentSeat = new()
    {
        Id = "agent",
        DisplayName = "Agent",
        UserName = "seat-agent",
        HostAddress = "pc",
        AgentControlEnabled = true,
        StreamingEnabled = false
    };

    private static AgentBatchRequest Batch(string json, bool screenshot = true)
    {
        Assert.True(AgentActionBatch.TryParse(JsonNode.Parse(json), out var batch, out var error), error);
        return batch! with { Screenshot = screenshot, SettleMilliseconds = 0 };
    }

    private static Fixture CreateFixture(
        SeatDefinition? seat = null,
        FakeSessions? sessions = null,
        FakeHost? host = null,
        FakePolicy? policy = null,
        AgentControlOptions? options = null)
    {
        seat ??= AgentSeat;
        sessions ??= new FakeSessions([new SeatSession(5, "seat-agent", "PC", "RDP", "RDP-Tcp#1", SeatSessionState.Active)]);
        var store = new MemoryStore(seat);
        var manager = new SeatManager(store, sessions);
        var anchors = new FakeAnchors();
        host ??= new FakeHost();
        var audit = new AgentAuditLog();
        policy ??= new FakePolicy();
        var share = new FakeShare();
        var service = new AgentControlService(
            manager,
            anchors,
            host,
            policy,
            sessions,
            share,
            audit,
            options ?? new AgentControlOptions
            {
                SessionStartTimeout = TimeSpan.FromMilliseconds(80),
                SessionPollInterval = TimeSpan.FromMilliseconds(10),
                QueueTimeout = TimeSpan.FromMilliseconds(200),
                CommandTimeout = TimeSpan.FromMilliseconds(300),
                CloseGateTimeout = TimeSpan.FromMilliseconds(100),
                LogoffTimeout = TimeSpan.FromMilliseconds(300)
            });
        return new Fixture(service, host, anchors, audit, seat, policy, sessions, share);
    }

    [Fact]
    public async Task RunsActionsInOrderThenReturnsOneScreenshot()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat,
            Batch("""[{ "type": "click", "x": 1, "y": 2 }, { "type": "type", "text": "hi" }]"""));

        Assert.True(result.Ok);
        Assert.Equal(2, result.Completed);
        Assert.Equal(
            [AgentActions.Click, AgentActions.Type, AgentActions.Screenshot],
            fixture.Host.Sent.Select(request => request.Action));
        Assert.Equal(1280, result.Screenshot!.Width);
        Assert.Equal("image/png", result.Screenshot.MimeType);
        Assert.Equal(1, fixture.Host.Launches);
    }

    [Fact]
    public async Task StopsAtTheFirstFailedActionButStillReturnsAScreenshot()
    {
        var fixture = CreateFixture();
        fixture.Host.FailAction = AgentActions.Type;

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat,
            Batch("""[{ "type": "click", "x": 1, "y": 2 }, { "type": "type", "text": "hi" }, { "type": "move", "x": 3, "y": 4 }]"""));

        Assert.False(result.Ok);
        Assert.Equal(1, result.FailedIndex);
        Assert.Equal("action_failed", result.ErrorCode);
        Assert.Equal(1, result.Completed);
        Assert.NotNull(result.Screenshot);
        Assert.DoesNotContain(fixture.Host.Sent, request => request.Action == AgentActions.MouseMove);
    }

    [Fact]
    public async Task SeatWithoutOptInIsRefusedBeforeAnythingRuns()
    {
        var fixture = CreateFixture(seat: AgentSeat with { AgentControlEnabled = false });

        var exception = await Assert.ThrowsAsync<AgentControlException>(() =>
            fixture.Service.ExecuteBatchAsync(fixture.Seat, Batch("""[{ "type": "wait", "ms": 5 }]""")));

        Assert.Equal(AgentControlException.Disabled, exception.Code);
        Assert.Empty(fixture.Host.Sent);
        Assert.Equal(0, fixture.Host.Launches);
    }

    [Fact]
    public async Task PausedSeatRefusesBatches()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.SetPausedAsync(fixture.Seat, paused: true);

        var exception = await Assert.ThrowsAsync<AgentControlException>(() =>
            fixture.Service.ExecuteBatchAsync(fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""")));

        Assert.Equal(AgentControlException.Paused, exception.Code);
        Assert.Empty(fixture.Host.Sent);

        _ = await fixture.Service.SetPausedAsync(fixture.Seat, paused: false);
        Assert.True((await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]"""))).Ok);
    }

    [Fact]
    public async Task PauseAppliesBetweenActionsOfARunningBatch()
    {
        var fixture = CreateFixture();
        fixture.Host.OnSend = request =>
        {
            if (request.Action == AgentActions.Click)
            {
                fixture.Service.SetPausedAsync(fixture.Seat, paused: true).GetAwaiter().GetResult();
            }
        };

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat,
            Batch("""[{ "type": "click", "x": 1, "y": 1 }, { "type": "type", "text": "x" }]"""));

        Assert.False(result.Ok);
        Assert.Equal(AgentControlException.Paused, result.ErrorCode);
        Assert.Equal(1, result.FailedIndex);
        Assert.Null(result.Screenshot);
        Assert.DoesNotContain(fixture.Host.Sent, request => request.Action == AgentActions.Type);
    }

    [Fact]
    public async Task SeatWithoutASessionReportsSessionUnavailable()
    {
        var fixture = CreateFixture(sessions: new FakeSessions([]));

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]"""));

        Assert.False(result.Ok);
        Assert.Equal(AgentControlException.SessionUnavailable, result.ErrorCode);
        Assert.Equal(0, fixture.Host.Launches);
    }

    [Fact]
    public async Task InvalidActionsAreRejectedBeforeAnyActionRuns()
    {
        var fixture = CreateFixture();
        var bad = new AgentBatchRequest(
            [
                new AgentBatchStep(0, new AgentRequest { Action = AgentActions.Click, X = 1, Y = 1 }),
                new AgentBatchStep(1, new AgentRequest { Action = AgentActions.Click, X = 999999, Y = 1 })
            ],
            Screenshot: false,
            SettleMilliseconds: 0,
            new AgentRequest { Action = AgentActions.Screenshot });

        var exception = await Assert.ThrowsAsync<AgentControlException>(() =>
            fixture.Service.ExecuteBatchAsync(fixture.Seat, bad));

        Assert.Equal(AgentControlException.InvalidRequest, exception.Code);
        Assert.StartsWith("Action 1:", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Host.Sent);
    }

    [Fact]
    public async Task UnreachableHelperIsRelaunchedOnceAndTheCommandRetried()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""", screenshot: false));
        fixture.Host.UnavailableOnce = true;

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 2, "y": 2 }]""", screenshot: false));

        Assert.True(result.Ok);
        Assert.Equal(2, fixture.Host.Launches);
    }

    [Fact]
    public async Task BrokenExchangeIsNotRetriedBecauseTheActionMayHaveRun()
    {
        var fixture = CreateFixture();
        fixture.Host.FailedExchange = true;

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""", screenshot: false));

        Assert.False(result.Ok);
        Assert.Equal(AgentControlException.HelperFailed, result.ErrorCode);
        Assert.Equal(1, fixture.Host.Sent.Count(request => request.Action == AgentActions.Click));
    }

    [Fact]
    public async Task SlowHelperTimesOut()
    {
        var fixture = CreateFixture();
        fixture.Host.Hang = true;

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""", screenshot: false));

        Assert.False(result.Ok);
        Assert.Equal(AgentControlException.Timeout, result.ErrorCode);
    }

    [Fact]
    public async Task SecondBatchWaitsForTheFirstAndTimesOutAsBusy()
    {
        var fixture = CreateFixture();
        fixture.Host.Hang = true;
        var first = fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""", screenshot: false));
        await fixture.Host.WaitUntilSendingAsync();

        var exception = await Assert.ThrowsAsync<AgentControlException>(() =>
            fixture.Service.ExecuteBatchAsync(
                fixture.Seat, Batch("""[{ "type": "click", "x": 2, "y": 2 }]""", screenshot: false)));

        Assert.Equal(AgentControlException.Busy, exception.Code);
        _ = await first;
    }

    [Fact]
    public async Task StopKillsTheHelperEvenWhileACommandHoldsTheGate()
    {
        var fixture = CreateFixture();
        fixture.Host.Hang = true;
        var running = fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""", screenshot: false));
        await fixture.Host.WaitUntilSendingAsync();

        var status = await fixture.Service.StopAsync(fixture.Seat);

        Assert.False(status.HelperRunning);
        Assert.Equal(1, fixture.Host.Stops);
        Assert.False((await running).Ok);
    }

    [Fact]
    public async Task StartBringsUpTheSessionThroughTheAnchorWhenMissing()
    {
        var sessions = new FakeSessions([]);
        var fixture = CreateFixture(sessions: sessions);
        fixture.Anchors.OnStart = () => sessions.Set(
            [new SeatSession(9, "seat-agent", "PC", "RDP", "RDP-Tcp#2", SeatSessionState.Active)]);

        var status = await fixture.Service.StartAsync(fixture.Seat);

        Assert.True(status.SessionAvailable);
        Assert.Equal(9, status.SessionId);
        Assert.True(status.HelperRunning);
        Assert.Equal(1, fixture.Anchors.Starts);
        Assert.Equal(1280, status.DesktopWidth);
    }

    [Fact]
    public async Task StartGivesUpWhenTheSessionNeverAppears()
    {
        var fixture = CreateFixture(sessions: new FakeSessions([]));

        var exception = await Assert.ThrowsAsync<AgentControlException>(() =>
            fixture.Service.StartAsync(fixture.Seat));

        Assert.Equal(AgentControlException.SessionUnavailable, exception.Code);
    }

    [Fact]
    public async Task AuditLogRecordsActionsWithoutTypedText()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat,
            Batch("""[{ "type": "type", "text": "hunter2-secret" }, { "type": "launch", "path": "notepad.exe", "arguments": ["--token=abc"] }]""",
                screenshot: false));

        var log = fixture.Service.GetLog(fixture.Seat, 10);

        Assert.Equal(2, log.Count);
        var serialized = string.Join('|', log.Select(entry => entry.Summary));
        Assert.DoesNotContain("hunter2", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", serialized, StringComparison.Ordinal);
        Assert.Contains("type 14 characters", serialized, StringComparison.Ordinal);
        Assert.Contains("launch notepad.exe", serialized, StringComparison.Ordinal);
    }

    // ---- regressions for the independent review of the first version ----

    [Fact]
    public async Task KillSwitchCancelsTheRestOfARunningBatchAndNeverRelaunchesTheHelper()
    {
        var fixture = CreateFixture();
        fixture.Host.OnSend = request =>
        {
            if (request.Action == AgentActions.Click)
            {
                fixture.Service.StopAsync(fixture.Seat).GetAwaiter().GetResult();
            }
        };

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat,
            Batch("""[{ "type": "click", "x": 1, "y": 1 }, { "type": "type", "text": "x" }]"""));

        Assert.False(result.Ok);
        Assert.Equal(AgentControlException.Stopped, result.ErrorCode);
        Assert.Equal(1, result.FailedIndex);
        Assert.Null(result.Screenshot);
        Assert.Equal(1, fixture.Host.Launches);
        Assert.DoesNotContain(fixture.Host.Sent, request => request.Action == AgentActions.Type);
    }

    [Fact]
    public async Task BatchesQueuedBehindARunningOneAreDroppedWhenTheOwnerPauses()
    {
        var fixture = CreateFixture(options: new AgentControlOptions
        {
            QueueTimeout = TimeSpan.FromSeconds(5),
            CommandTimeout = TimeSpan.FromMilliseconds(400)
        });
        fixture.Host.Hang = true;
        var first = fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""", screenshot: false));
        await fixture.Host.WaitUntilSendingAsync();
        var queued = fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "type", "text": "queued" }]""", screenshot: false));
        await Task.Delay(50);

        _ = await fixture.Service.SetPausedAsync(fixture.Seat, paused: true);

        _ = await first;
        var exception = await Assert.ThrowsAsync<AgentControlException>(() => queued);
        Assert.Equal(AgentControlException.Paused, exception.Code);
        Assert.DoesNotContain(fixture.Host.Sent, request => request.Action == AgentActions.Type);
    }

    [Fact]
    public async Task StartIsRefusedWhilePaused()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.SetPausedAsync(fixture.Seat, paused: true);

        var exception = await Assert.ThrowsAsync<AgentControlException>(() => fixture.Service.StartAsync(fixture.Seat));

        Assert.Equal(AgentControlException.Paused, exception.Code);
        Assert.Equal(0, fixture.Host.Launches);
    }

    [Fact]
    public async Task KillSwitchNeverTalksToTheHelperSoAFrozenOneCannotBlockIt()
    {
        var fixture = CreateFixture();

        var status = await fixture.Service.StopAsync(fixture.Seat);

        Assert.False(status.HelperRunning);
        Assert.Equal(0, fixture.Host.AttachCalls);
        Assert.Empty(fixture.Host.Sent);
        Assert.Equal(1, fixture.Host.StopInSessionCalls);
    }

    [Fact]
    public async Task SeatThatIsNotOnTheAdministratorsListIsRefusedBeforeAnythingRuns()
    {
        var fixture = CreateFixture(policy: new FakePolicy { SeatRefusal = "not approved by an administrator" });

        var exception = await Assert.ThrowsAsync<AgentControlException>(() =>
            fixture.Service.ExecuteBatchAsync(fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""")));
        var start = await Assert.ThrowsAsync<AgentControlException>(() => fixture.Service.StartAsync(fixture.Seat));

        Assert.Equal(AgentControlException.Disabled, exception.Code);
        Assert.Contains("not approved", exception.Message, StringComparison.Ordinal);
        Assert.Equal(AgentControlException.Disabled, start.Code);
        Assert.Equal(0, fixture.Host.Launches);
        Assert.Empty(fixture.Host.Sent);
    }

    [Fact]
    public async Task ARefusedSessionIsNeverDrivenAndStatusSaysWhy()
    {
        var fixture = CreateFixture(policy: new FakePolicy { SessionRefusal = "that is the physical console" });

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""", screenshot: false));
        var status = await fixture.Service.GetStatusAsync(fixture.Seat);

        Assert.False(result.Ok);
        Assert.Equal(AgentControlException.Disabled, result.ErrorCode);
        Assert.Equal(0, fixture.Host.Launches);
        Assert.False(status.SessionAvailable);
        Assert.Contains("physical console", status.Detail, StringComparison.Ordinal);
        Assert.True(status.Refused);
    }

    [Fact]
    public async Task ApprovalIsCheckedAgainBeforeEveryActionOfARunningBatch()
    {
        var fixture = CreateFixture();
        fixture.Host.OnSend = request =>
        {
            if (request.Action == AgentActions.Click)
            {
                fixture.Policy.SeatRefusal = "approval withdrawn";
            }
        };

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat,
            Batch("""[{ "type": "click", "x": 1, "y": 1 }, { "type": "type", "text": "x" }]""", screenshot: false));

        Assert.False(result.Ok);
        Assert.Equal(AgentControlException.Disabled, result.ErrorCode);
        Assert.Equal(1, result.FailedIndex);
        Assert.DoesNotContain(fixture.Host.Sent, request => request.Action == AgentActions.Type);
    }

    [Fact]
    public async Task ActionsThatAllRanStillSucceedWhenOnlyTheScreenshotFails()
    {
        var fixture = CreateFixture();
        fixture.Host.BadScreenshot = "mime";

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]"""));

        // A retrying caller would replay the click if this were reported as a failure.
        Assert.True(result.Ok);
        Assert.Equal(1, result.Completed);
        Assert.Null(result.Screenshot);
        Assert.NotNull(result.ScreenshotError);
    }

    [Theory]
    [InlineData("mime")]
    [InlineData("base64")]
    public async Task AScreenshotOnlyRequestFailsWhenTheHelperReturnsAnUnusableImage(string defect)
    {
        var fixture = CreateFixture();
        fixture.Host.BadScreenshot = defect;

        var result = await fixture.Service.ExecuteBatchAsync(fixture.Seat, Batch("""{ "actions": [] }"""));

        Assert.False(result.Ok);
        Assert.Null(result.Screenshot);
        Assert.NotNull(result.ErrorCode);
    }

    [Fact]
    public async Task AnUnreasonablyLargeHelperResultIsRejected()
    {
        var fixture = CreateFixture();
        fixture.Host.HugeInfoResult = true;

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "windows" }]""", screenshot: false));

        Assert.False(result.Ok);
        Assert.Equal(AgentControlException.HelperFailed, result.ErrorCode);
    }

    // ---- closing the seat and the file share ----

    [Fact]
    public async Task CloseStopsTheHelperAndAnchorLogsTheSessionOffAndWipesTheShare()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""", screenshot: false));

        var result = await fixture.Service.CloseAsync(fixture.Seat, wipeFiles: true);

        Assert.True(result.Closed);
        Assert.Equal(5, result.SessionId);
        Assert.Equal(3, result.FilesRemoved);
        Assert.Equal([5], fixture.Sessions.LoggedOff);
        Assert.Equal(1, fixture.Anchors.Stops);
        Assert.Equal(1, fixture.Share.Clears);
        Assert.True(fixture.Host.StopInSessionCalls >= 1);
        Assert.False((await fixture.Service.GetStatusAsync(fixture.Seat)).SessionAvailable);
    }

    [Fact]
    public async Task CloseCanKeepTheFiles()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.CloseAsync(fixture.Seat, wipeFiles: false);

        Assert.True(result.Closed);
        Assert.Equal(0, result.FilesRemoved);
        Assert.Equal(0, fixture.Share.Clears);
    }

    [Fact]
    public async Task CloseOfAnAlreadyClosedSeatStillStopsTheAnchorSoItStaysClosed()
    {
        var fixture = CreateFixture(sessions: new FakeSessions([]));

        var result = await fixture.Service.CloseAsync(fixture.Seat, wipeFiles: true);

        Assert.True(result.Closed);
        Assert.Null(result.SessionId);
        Assert.Empty(fixture.Sessions.LoggedOff);
        Assert.Equal(1, fixture.Anchors.Stops);
    }

    [Fact]
    public async Task CloseIsRefusedWhileABatchIsRunningInsteadOfCuttingItOff()
    {
        var fixture = CreateFixture(options: new AgentControlOptions
        {
            QueueTimeout = TimeSpan.FromSeconds(5),
            CommandTimeout = TimeSpan.FromSeconds(2),
            CloseGateTimeout = TimeSpan.FromMilliseconds(100)
        });
        fixture.Host.Hang = true;
        using var cancel = new CancellationTokenSource();
        var running = fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""", screenshot: false), cancel.Token);
        await fixture.Host.WaitUntilSendingAsync();

        var exception = await Assert.ThrowsAsync<AgentControlException>(() =>
            fixture.Service.CloseAsync(fixture.Seat, wipeFiles: true));

        Assert.Equal(AgentControlException.Busy, exception.Code);
        Assert.Empty(fixture.Sessions.LoggedOff);
        Assert.Equal(0, fixture.Share.Clears);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task ClosingAndCleaningAreRefusedWhilePaused()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.SetPausedAsync(fixture.Seat, paused: true);

        var close = await Assert.ThrowsAsync<AgentControlException>(() =>
            fixture.Service.CloseAsync(fixture.Seat, wipeFiles: true));
        var clean = await Assert.ThrowsAsync<AgentControlException>(() => fixture.Service.CleanFilesAsync(fixture.Seat));

        Assert.Equal(AgentControlException.Paused, close.Code);
        Assert.Equal(AgentControlException.Paused, clean.Code);
        Assert.Empty(fixture.Sessions.LoggedOff);
        Assert.Equal(0, fixture.Share.Clears);
    }

    [Fact]
    public async Task TheConsoleSessionIsNeverLoggedOff()
    {
        var fixture = CreateFixture(policy: new FakePolicy { SessionRefusal = "that is the physical console" });

        var exception = await Assert.ThrowsAsync<AgentControlException>(() =>
            fixture.Service.CloseAsync(fixture.Seat, wipeFiles: true));

        Assert.Equal(AgentControlException.Disabled, exception.Code);
        Assert.Empty(fixture.Sessions.LoggedOff);
        Assert.Equal(0, fixture.Anchors.Stops);
    }

    [Fact]
    public async Task ASeatThatIsNotApprovedCannotBeClosedOrCleanedByTheAgent()
    {
        var fixture = CreateFixture(policy: new FakePolicy { SeatRefusal = "not approved" });

        await Assert.ThrowsAsync<AgentControlException>(() => fixture.Service.CloseAsync(fixture.Seat, wipeFiles: true));
        await Assert.ThrowsAsync<AgentControlException>(() => fixture.Service.CleanFilesAsync(fixture.Seat));

        Assert.Empty(fixture.Sessions.LoggedOff);
        Assert.Equal(0, fixture.Share.Clears);
    }

    [Fact]
    public async Task AShareThatCannotBeWipedSafelyDoesNotUndoAClose()
    {
        var fixture = CreateFixture();
        fixture.Share.Unsafe = true;

        var result = await fixture.Service.CloseAsync(fixture.Seat, wipeFiles: true);

        Assert.True(result.Closed);
        Assert.Equal(1, result.FilesFailed);
        Assert.Equal([5], fixture.Sessions.LoggedOff);
    }

    [Fact]
    public async Task AnExplicitCleanOfAnUnsafeShareIsAnError()
    {
        var fixture = CreateFixture();
        fixture.Share.Unsafe = true;

        var exception = await Assert.ThrowsAsync<AgentControlException>(() => fixture.Service.CleanFilesAsync(fixture.Seat));

        Assert.Equal(AgentControlException.ShareUnavailable, exception.Code);
    }

    [Fact]
    public async Task StatusReportsWhereTheShareFolderIs()
    {
        var fixture = CreateFixture();

        var status = await fixture.Service.GetStatusAsync(fixture.Seat);

        Assert.Equal(@"C:\share\agent", status.SharePath);
        Assert.False(status.Refused);
    }

    [Fact]
    public async Task StatusOfAnUnapprovedSeatSaysItIsRefusedAndWhy()
    {
        var fixture = CreateFixture(policy: new FakePolicy { SeatRefusal = "Seat 'agent' is not approved for agent control." });

        var status = await fixture.Service.GetStatusAsync(fixture.Seat);

        Assert.True(status.Refused);
        Assert.Contains("not approved", status.Detail, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Host.AttachCalls);
    }

    [Fact]
    public async Task CloseAndCleanAreWrittenToTheAuditLogWithoutFileNames()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.CleanFilesAsync(fixture.Seat);
        _ = await fixture.Service.CloseAsync(fixture.Seat, wipeFiles: true);

        var summaries = fixture.Service.GetLog(fixture.Seat, 10).Select(entry => entry.Summary).ToArray();

        Assert.Contains(summaries, summary => summary.StartsWith("close seat", StringComparison.Ordinal));
        Assert.Contains(summaries, summary => summary.StartsWith("wipe share", StringComparison.Ordinal));
    }

    // ---- held input (key_down / mouse_down) ----

    private static async Task WaitForLogAsync(Fixture fixture, Func<AgentAuditEntry, bool> match)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (fixture.Service.GetLog(fixture.Seat, 50).Any(match))
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The expected audit entry never appeared.");
    }

    [Fact]
    public async Task PauseLetsGoOfInputThatKeyDownLeftHeld()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "key_down", "keys": "shift" }]""", screenshot: false));

        _ = await fixture.Service.SetPausedAsync(fixture.Seat, paused: true);

        await WaitForLogAsync(fixture, entry => entry.Action == AgentActions.Release);
        Assert.Contains(fixture.Host.Sent, request => request.Action == AgentActions.Release);
        Assert.True(fixture.Service.GetLog(fixture.Seat, 50).First(entry => entry.Action == AgentActions.Release).Ok);
    }

    [Fact]
    public async Task PauseSendsNothingWhenNothingIsHeld()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat,
            Batch("""[{ "type": "key_down", "keys": "shift" }, { "type": "key_up" }, { "type": "click", "x": 1, "y": 1 }]""", screenshot: false));

        _ = await fixture.Service.SetPausedAsync(fixture.Seat, paused: true);
        await Task.Delay(100);

        Assert.DoesNotContain(fixture.Host.Sent, request => request.Action == AgentActions.Release);
    }

    [Fact]
    public async Task KillSwitchReleasesHeldInputThroughAOneShotHelper()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "mouse_down", "x": 3, "y": 4 }]""", screenshot: false));

        _ = await fixture.Service.StopAsync(fixture.Seat);

        Assert.Equal([5], fixture.Host.ReleaseLaunches);
        Assert.Contains("held input released", fixture.Service.GetLog(fixture.Seat, 1)[0].Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Host.Sent, request => request.Action == AgentActions.Release);
    }

    [Fact]
    public async Task KillingAHelperAlwaysReleasesBecauseAHoldOrClickMayHaveBeenCutOff()
    {
        var fixture = CreateFixture();
        fixture.Host.Hang = true;
        var running = fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "hold", "keys": "w", "ms": 5000 }]""", screenshot: false));
        await fixture.Host.WaitUntilSendingAsync();

        _ = await fixture.Service.StopAsync(fixture.Seat);

        Assert.Equal([5], fixture.Host.ReleaseLaunches);
        Assert.False((await running).Ok);
    }

    [Fact]
    public async Task KillSwitchWithNoHelperAndNothingHeldLaunchesNothing()
    {
        var fixture = CreateFixture();

        _ = await fixture.Service.StopAsync(fixture.Seat);

        Assert.Empty(fixture.Host.ReleaseLaunches);
    }

    [Fact]
    public async Task PressingTheKillSwitchAgainReplacesTheReleaseHelperItKilled()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "key_down", "keys": "w" }]""", screenshot: false));

        _ = await fixture.Service.StopAsync(fixture.Seat);
        _ = await fixture.Service.StopAsync(fixture.Seat);

        Assert.Equal([5, 5], fixture.Host.ReleaseLaunches);
    }

    [Fact]
    public async Task PauseReleasesAKeyDownThatWasInFlightWhenItLanded()
    {
        var fixture = CreateFixture();
        fixture.Host.OnSend = request =>
        {
            if (request.Action == AgentActions.KeyDown)
            {
                fixture.Service.SetPausedAsync(fixture.Seat, paused: true).GetAwaiter().GetResult();
            }
        };

        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "key_down", "keys": "w" }, { "type": "type", "text": "x" }]""", screenshot: false));

        await WaitForLogAsync(fixture, entry => entry.Action == AgentActions.Release);
        Assert.Contains(fixture.Host.Sent, request => request.Action == AgentActions.Release);
        Assert.DoesNotContain(fixture.Host.Sent, request => request.Action == AgentActions.Type);
    }

    [Fact]
    public async Task PauseFindsTheHelperAgainWhenTheServiceLostTrackOfIt()
    {
        var fixture = CreateFixture();
        fixture.Host.FailedExchange = true; // the answer to key_down is lost; the helper is still alive and holding
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "key_down", "keys": "w" }]""", screenshot: false));
        fixture.Host.FailedExchange = false;

        _ = await fixture.Service.SetPausedAsync(fixture.Seat, paused: true);

        await WaitForLogAsync(fixture, entry => entry.Action == AgentActions.Release);
        Assert.Contains(fixture.Host.Sent, request => request.Action == AgentActions.Release);
        Assert.Empty(fixture.Host.ReleaseLaunches);
    }

    [Fact]
    public async Task PauseUsesTheOneShotHelperWhenTheHelperIsGone()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "mouse_down" }]""", screenshot: false));
        fixture.Host.Crash();

        _ = await fixture.Service.SetPausedAsync(fixture.Seat, paused: true);

        await WaitForLogAsync(fixture, entry => entry.Action == AgentActions.Release);
        Assert.Equal([5], fixture.Host.ReleaseLaunches);
        Assert.Equal(1, fixture.Host.Launches); // never a new full helper while paused
    }

    [Fact]
    public async Task AReleaseThatWindowsRefusedIsReportedAndStillCountsAsHeld()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "key_down", "keys": "w" }]""", screenshot: false));
        fixture.Host.ReleaseRefused = true;

        _ = await fixture.Service.SetPausedAsync(fixture.Seat, paused: true);
        await WaitForLogAsync(fixture, entry => entry.Action == AgentActions.Release);
        fixture.Host.Crash();
        _ = await fixture.Service.StopAsync(fixture.Seat);

        Assert.False(fixture.Service.GetLog(fixture.Seat, 50).First(entry => entry.Action == AgentActions.Release).Ok);
        Assert.Equal([5], fixture.Host.ReleaseLaunches); // nothing was killed, but the input is still known to be held
    }

    [Fact]
    public async Task AStepInFlightWhenTheKillSwitchLandsNeverBringsAHelperBack()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "click", "x": 1, "y": 1 }]""", screenshot: false));
        // The helper turns out to be unreachable, which normally means "relaunch it and retry"; the owner pressed
        // stop at that same moment.
        fixture.Host.UnavailableOnce = true;
        fixture.Host.OnUnavailable = () => fixture.Service.StopAsync(fixture.Seat).GetAwaiter().GetResult();

        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "key_down", "keys": "w" }]""", screenshot: false));

        Assert.False(result.Ok);
        Assert.Equal(AgentControlException.Stopped, result.ErrorCode);
        Assert.Equal(1, fixture.Host.Launches);
        Assert.DoesNotContain(fixture.Host.Sent, request => request.Action == AgentActions.KeyDown);
    }

    [Fact]
    public async Task AKeyDownWhoseAnswerWasLostStillCountsAsHeld()
    {
        var fixture = CreateFixture();
        fixture.Host.FailedExchange = true;
        var result = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "key_down", "keys": "w" }]""", screenshot: false));
        Assert.False(result.Ok);

        _ = await fixture.Service.StopAsync(fixture.Seat);

        Assert.Equal([5], fixture.Host.ReleaseLaunches);
    }

    [Fact]
    public async Task KillSwitchNeverLaunchesTheReleaseHelperIntoARefusedSession()
    {
        var fixture = CreateFixture();
        _ = await fixture.Service.ExecuteBatchAsync(
            fixture.Seat, Batch("""[{ "type": "key_down", "keys": "w" }]""", screenshot: false));
        fixture.Policy.SessionRefusal = "that is the physical console";

        _ = await fixture.Service.StopAsync(fixture.Seat);

        Assert.Empty(fixture.Host.ReleaseLaunches);
    }

    // ---- change reports and zoom ----

    private static string Signature(byte blue = 0)
    {
        var pixels = new byte[32 * 32 * 4];
        pixels[0] = blue;
        return AgentFrameSignature.Compute(pixels, 32 * 4, new AgentRegion(0, 0, 32, 32)).Encode();
    }

    [Fact]
    public async Task AChangeReportIsRelayedWithOnlyWellFormedCrops()
    {
        var fixture = CreateFixture();
        fixture.Host.ScreenshotExtras = new JsonObject
        {
            ["signature"] = Signature(),
            ["changed"] = true,
            ["changes"] = new JsonArray(
                new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 32, ["height"] = 32, ["mimeType"] = "image/png", ["dataBase64"] = "AAAA" },
                new JsonObject { ["x"] = 64, ["y"] = 0, ["width"] = 32, ["height"] = 32, ["mimeType"] = "text/html", ["dataBase64"] = "AAAA" })
        };
        var batch = Batch("""[{ "type": "click", "x": 1, "y": 1 }]""") with
        {
            ScreenshotOptions = new AgentRequest { Action = AgentActions.Screenshot, Diff = true, Since = Signature(blue: 9) }
        };

        var screenshot = (await fixture.Service.ExecuteBatchAsync(fixture.Seat, batch)).Screenshot!;

        Assert.Equal(Signature(), screenshot.Signature);
        Assert.True(screenshot.Changed);
        Assert.Equal(2, screenshot.Changes!.Count);
        Assert.Equal("AAAA", screenshot.Changes[0].DataBase64);
        Assert.Null(screenshot.Changes[1].DataBase64);
        Assert.Null(screenshot.Changes[1].MimeType);
        Assert.Equal(64, screenshot.Changes[1].X);
    }

    [Fact]
    public async Task AChangeReportNobodyAskedForIsDropped()
    {
        var fixture = CreateFixture();
        fixture.Host.ScreenshotExtras = new JsonObject
        {
            ["signature"] = Signature(),
            ["changed"] = false,
            ["changes"] = new JsonArray()
        };

        var screenshot = (await fixture.Service.ExecuteBatchAsync(fixture.Seat, Batch("""{ "actions": [] }"""))).Screenshot!;

        Assert.Null(screenshot.Changed);
        Assert.Null(screenshot.Changes);
        Assert.Equal(Signature(), screenshot.Signature);
    }

    [Fact]
    public async Task AMalformedSignatureOrChangeListFromTheHelperIsDropped()
    {
        var fixture = CreateFixture();
        fixture.Host.ScreenshotExtras = new JsonObject
        {
            ["signature"] = "t32:not-a-signature",
            ["changed"] = true,
            ["changes"] = new JsonArray(new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 0, ["height"] = 5 })
        };
        var batch = Batch("""{ "actions": [] }""") with
        {
            ScreenshotOptions = new AgentRequest { Action = AgentActions.Screenshot, Diff = true, Since = Signature() }
        };

        var screenshot = (await fixture.Service.ExecuteBatchAsync(fixture.Seat, batch)).Screenshot!;

        Assert.NotNull(screenshot.DataBase64);
        Assert.Null(screenshot.Signature);
        Assert.Null(screenshot.Changed);
        Assert.Null(screenshot.Changes);
    }

    [Fact]
    public async Task AZoomReportsWhichAreaTheImageShows()
    {
        var fixture = CreateFixture();
        fixture.Host.ScreenshotExtras = new JsonObject
        {
            ["regionX"] = 100,
            ["regionY"] = 50,
            ["regionWidth"] = 300,
            ["regionHeight"] = 200
        };

        var screenshot = (await fixture.Service.ExecuteBatchAsync(
            fixture.Seat,
            Batch("""[{ "type": "zoom", "region": [100, 50, 400, 250] }]"""))).Screenshot!;

        Assert.Equal(new AgentRegion(100, 50, 300, 200), screenshot.Region);
        Assert.Equal(new AgentRegion(100, 50, 300, 200), fixture.Host.Sent.Single(request => request.Action == AgentActions.Screenshot).Region);
    }

    private sealed record Fixture(
        AgentControlService Service,
        FakeHost Host,
        FakeAnchors Anchors,
        AgentAuditLog Audit,
        SeatDefinition Seat,
        FakePolicy Policy,
        FakeSessions Sessions,
        FakeShare Share);

    private sealed class FakeShare : IAgentFileShare
    {
        public int Clears { get; private set; }

        public bool Unsafe { get; set; }

        public string RootFor(SeatDefinition seat) => $@"C:\share\{seat.Id}";

        public AgentShareClearResult Clear(SeatDefinition seat)
        {
            if (Unsafe)
            {
                throw new InvalidOperationException("the share folder is a link");
            }

            Clears++;
            return new AgentShareClearResult(3, 0);
        }
    }

    private sealed class FakePolicy : IAgentSeatPolicy
    {
        public string? SeatRefusal { get; set; }

        public string? SessionRefusal { get; set; }

        public string? Refusal(SeatDefinition seat) => SeatRefusal;

        public string? RefusalForSession(SeatSession session) => SessionRefusal;
    }

    private sealed class FakeHost : IAgentHelperHost
    {
        private readonly TaskCompletionSource _sending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nextProcessId = 100;
        private AgentHelperInfo? _running;

        public List<AgentRequest> Sent { get; } = [];

        public int Launches { get; private set; }

        public int Stops { get; private set; }

        public string? FailAction { get; set; }

        public bool UnavailableOnce { get; set; }

        public bool FailedExchange { get; set; }

        public bool Hang { get; set; }

        public Action<AgentRequest>? OnSend { get; set; }

        public Action? OnUnavailable { get; set; }

        public int AttachCalls { get; private set; }

        public int StopInSessionCalls { get; private set; }

        /// <summary>Makes screenshots carry a broken payload (<c>"mime"</c> or <c>"base64"</c>).</summary>
        public string? BadScreenshot { get; set; }

        public bool HugeInfoResult { get; set; }

        public Task WaitUntilSendingAsync() => _sending.Task;

        public Task<AgentHelperInfo?> TryAttachAsync(SeatDefinition seat, int sessionId, CancellationToken cancellationToken)
        {
            AttachCalls++;
            return Task.FromResult(_running is { } helper && helper.SessionId == sessionId ? helper : null);
        }

        public int StopInSession(int sessionId)
        {
            StopInSessionCalls++;
            // Like the real host, this kills a one-shot release helper too: it is the same executable.
            var stopped = (_running is null ? 0 : 1) + (_releaseRunning ? 1 : 0);
            _running = null;
            _releaseRunning = false;
            return stopped;
        }

        private bool _releaseRunning;

        public List<int> ReleaseLaunches { get; } = [];

        public bool ReleaseInputs(SeatDefinition seat, int sessionId)
        {
            ReleaseLaunches.Add(sessionId);
            _releaseRunning = true;
            return true;
        }

        /// <summary>The helper process dies on its own (the service is not told).</summary>
        public void Crash() => _running = null;

        /// <summary>Windows refuses the key-ups, so the helper still reports the input as held after a release.</summary>
        public bool ReleaseRefused { get; set; }

        public Task<AgentHelperInfo> LaunchAsync(SeatDefinition seat, int sessionId, CancellationToken cancellationToken)
        {
            Launches++;
            _running = new AgentHelperInfo(_nextProcessId++, sessionId, DateTimeOffset.UtcNow);
            return Task.FromResult(_running);
        }

        public async Task<AgentResponse> SendAsync(
            SeatDefinition seat,
            AgentHelperInfo helper,
            AgentRequest request,
            CancellationToken cancellationToken)
        {
            if (UnavailableOnce)
            {
                UnavailableOnce = false;
                _running = null;
                OnUnavailable?.Invoke();
                throw new AgentControlException(AgentControlException.HelperUnavailable, "pipe gone");
            }

            Sent.Add(request);
            _sending.TrySetResult();
            OnSend?.Invoke(request);
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (FailedExchange)
            {
                throw new AgentControlException(AgentControlException.HelperFailed, "pipe broke mid-command");
            }

            if (request.Action == FailAction)
            {
                return AgentResponse.Failure("action_failed", "nope");
            }

            if (request.Action == AgentActions.Windows && HugeInfoResult)
            {
                return AgentResponse.Success(new JsonObject { ["blob"] = new string('x', 1_200_000) });
            }

            // Like the real helper: input actions that can hold report what is still held afterwards.
            if (request.Action is AgentActions.KeyDown or AgentActions.MouseDown ||
                (request.Action == AgentActions.Release && ReleaseRefused))
            {
                return AgentResponse.Success(new JsonObject { ["held"] = new JsonArray("shift") });
            }

            if (request.Action is AgentActions.KeyUp or AgentActions.MouseUp or AgentActions.Release or AgentActions.Hold)
            {
                return AgentResponse.Success(new JsonObject { ["held"] = new JsonArray() });
            }

            if (request.Action is AgentActions.Screenshot or AgentActions.Info)
            {
                var data = new JsonObject
                {
                    ["mimeType"] = request.Action == AgentActions.Screenshot && BadScreenshot == "mime" ? "text/html" : "image/png",
                    ["width"] = 1280,
                    ["height"] = 800,
                    ["desktopWidth"] = 1280,
                    ["desktopHeight"] = 800,
                    ["dataBase64"] = request.Action == AgentActions.Screenshot && BadScreenshot == "base64" ? "!!!not base64!!!" : "AAAA"
                };
                if (request.Action == AgentActions.Screenshot && ScreenshotExtras is { } extras)
                {
                    foreach (var (name, value) in extras)
                    {
                        data[name] = value?.DeepClone();
                    }
                }

                return AgentResponse.Success(data);
            }

            return AgentResponse.Success();
        }

        /// <summary>Extra fields the helper puts into every screenshot answer (signature, change report, region).</summary>
        public JsonObject? ScreenshotExtras { get; set; }

        public bool IsRunning(AgentHelperInfo helper) => _running == helper;

        public bool Stop(AgentHelperInfo helper)
        {
            Stops++;
            var wasRunning = _running == helper; // like the real host: an exited process is not "stopped"
            _running = null;
            return wasRunning;
        }
    }

    private sealed class FakeAnchors : ISeatAnchorService
    {
        public int Starts { get; private set; }

        public Action? OnStart { get; set; }

        public Task StartAsync(SeatDefinition seat, CancellationToken cancellationToken = default)
        {
            Starts++;
            OnStart?.Invoke();
            return Task.CompletedTask;
        }

        public int Stops { get; private set; }

        public Task<bool> StopAsync(SeatDefinition seat, CancellationToken cancellationToken = default)
        {
            Stops++;
            return Task.FromResult(true);
        }

    }

    private sealed class MemoryStore(SeatDefinition seat) : ISeatStore
    {
        public Task<IReadOnlyList<SeatDefinition>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SeatDefinition>>([seat]);

        public Task<SeatDefinition?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<SeatDefinition?>(string.Equals(id, seat.Id, StringComparison.OrdinalIgnoreCase) ? seat : null);

        public Task UpsertAsync(SeatDefinition value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeSessions(IReadOnlyList<SeatSession> initial) : IWindowsSessionService
    {
        private IReadOnlyList<SeatSession> _sessions = initial;

        public void Set(IReadOnlyList<SeatSession> sessions) => _sessions = sessions;

        public Task<IReadOnlyList<SeatSession>> GetSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_sessions);

        public Task DisconnectAsync(int sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public List<int> LoggedOff { get; } = [];

        public Task LogoffAsync(int sessionId, CancellationToken cancellationToken = default)
        {
            LoggedOff.Add(sessionId);
            _sessions = _sessions.Where(session => session.SessionId != sessionId).ToArray();
            return Task.CompletedTask;
        }
    }

}
