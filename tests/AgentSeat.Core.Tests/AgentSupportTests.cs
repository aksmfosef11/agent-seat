using System.Text.Json;
using AgentSeat.Core.Agent;
using AgentSeat.Core.Models;
using AgentSeat.Core.Storage;

namespace AgentSeat.Core.Tests;

public sealed class AgentSupportTests
{
    [Fact]
    public async Task FramingRoundTripsMessagesAndReportsCleanEndOfStream()
    {
        using var stream = new MemoryStream();
        await AgentFraming.WriteJsonAsync(stream, new AgentRequest { Action = AgentActions.Type, Text = "héllo 안녕" }, default);
        await AgentFraming.WriteJsonAsync(stream, AgentResponse.Failure("x", "y"), default);
        stream.Position = 0;

        var request = await AgentFraming.ReadJsonAsync<AgentRequest>(stream, default);
        var response = await AgentFraming.ReadJsonAsync<AgentResponse>(stream, default);

        Assert.Equal("héllo 안녕", request!.Text);
        Assert.False(response!.Ok);
        Assert.Null(await AgentFraming.ReadAsync(stream, default));
    }

    [Fact]
    public async Task FramingRejectsTruncatedAndOversizedMessages()
    {
        using var truncated = new MemoryStream([10, 0, 0, 0, 1, 2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentFraming.ReadAsync(truncated, default));

        using var oversized = new MemoryStream(BitConverter.GetBytes(AgentFraming.MaxMessageBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentFraming.ReadAsync(oversized, default));

        using var negative = new MemoryStream(BitConverter.GetBytes(-1));
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentFraming.ReadAsync(negative, default));
    }

    [Theory]
    [InlineData("friend", true)]
    [InlineData("agent-1", true)]
    [InlineData("..\\evil", false)]
    [InlineData("a/b", false)]
    [InlineData("Upper", false)]
    [InlineData("", false)]
    public void PipeNamesAcceptOnlyValidSeatIds(string seatId, bool valid)
    {
        if (valid)
        {
            Assert.Equal($"AgentSeat.Agent.{seatId}", AgentPipe.NameFor(seatId));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => AgentPipe.NameFor(seatId));
        }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    [InlineData("", "\"\"")]
    public void ArgumentsAreQuotedForCommandLines(string value, string expected) =>
        Assert.Equal(expected, AgentPipe.JoinArguments([value]));

    [Fact]
    public void AuditLogKeepsTheNewestEntriesPerSeatWithinItsCapacity()
    {
        var log = new AgentAuditLog(capacity: 3);
        for (var index = 0; index < 5; index++)
        {
            log.Add(new AgentAuditEntry(DateTimeOffset.UtcNow, index % 2 == 0 ? "a" : "b", "click", $"#{index}", true, null, 1));
        }

        // Capacity counts every seat, newest first, and lookups are per seat.
        Assert.Equal(["#4", "#2"], log.Recent("a", 10).Select(entry => entry.Summary));
        Assert.Equal(["#3"], log.Recent("B", 10).Select(entry => entry.Summary));
    }

    [Fact]
    public void ValidatorChecksEveryActionsRequiredFields()
    {
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = "bogus" }));
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.FocusWindow }));
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Launch, Path = "bad\npath" }));
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Wait, Milliseconds = 0 }));
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Screenshot, Scale = 0 }));
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Click, X = 1, Y = 1, Modifiers = ["hyper"] }));
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Scroll, Direction = "sideways" }));
        Assert.NotNull(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Type, Text = new string('a', AgentRequestValidator.MaxTextLength + 1) }));

        Assert.Null(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Click, Button = "right", Clicks = 2, X = -5, Y = 7, Modifiers = ["CTRL", "shift"] }));
        Assert.Null(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Drag, Points = [new AgentPoint(1, 1), new AgentPoint(9, 9)] }));
        Assert.Null(AgentRequestValidator.Validate(new AgentRequest { Action = AgentActions.Launch, Path = "https://example.com" }));
    }

    [Fact]
    public async Task SeatsStoredBeforeAgentControlExistedStayDisabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-seat-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                { "schemaVersion": 2, "seats": [ {
                    "id": "friend", "displayName": "Friend", "userName": "seat-friend", "hostAddress": "pc",
                    "rdpPort": 3389, "width": 1920, "height": 1080, "streamingEnabled": true,
                    "sunshineBasePort": 47989, "enabled": true, "createdAtUtc": "2026-08-27T00:00:00+00:00" } ] }
                """);
            using var store = new JsonSeatStore(path);

            var friend = (await store.GetAsync("friend"))!;
            Assert.False(friend.AgentControlEnabled);

            await store.UpsertAsync(friend with { AgentControlEnabled = true });
            Assert.True((await store.GetAsync("friend"))!.AgentControlEnabled);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(document.RootElement.GetProperty("seats")[0].GetProperty("agentControlEnabled").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
