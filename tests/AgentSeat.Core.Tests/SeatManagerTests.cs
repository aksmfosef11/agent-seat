using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;
using AgentSeat.Core.Services;

namespace AgentSeat.Core.Tests;

public sealed class SeatManagerTests
{
    [Fact]
    public async Task Create_NormalizesIdAndAssociatesWindowsSession()
    {
        var store = new MemorySeatStore();
        var sessions = new FakeSessions(
        [
            new SeatSession(4, "seat-friend", "GAMINGPC", "FRIEND-PC", "RDP-Tcp#2", SeatSessionState.Active)
        ]);
        var manager = new SeatManager(store, sessions);

        await manager.CreateAsync(ValidSeat() with { Id = "Friend-One", UserName = @"GAMINGPC\seat-friend" });
        var view = Assert.Single(await manager.GetViewsAsync());

        Assert.Equal("friend-one", view.Seat.Id);
        Assert.Equal(47989, view.Seat.SunshineBasePort);
        Assert.True(view.Online);
        Assert.Equal(4, Assert.Single(view.Sessions).SessionId);
    }

    [Fact]
    public async Task Create_RejectsDuplicateWindowsUser()
    {
        var store = new MemorySeatStore();
        var manager = new SeatManager(store, new FakeSessions([]));
        await manager.CreateAsync(ValidSeat());

        var exception = await Assert.ThrowsAsync<SeatValidationException>(() =>
            manager.CreateAsync(ValidSeat() with { Id = "other", UserName = @"MACHINE\seat-friend" }));

        Assert.Contains("already assigned", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-bad")]
    [InlineData("has space")]
    [InlineData("a_underscore")]
    public async Task Create_RejectsInvalidId(string id)
    {
        var manager = new SeatManager(new MemorySeatStore(), new FakeSessions([]));

        await Assert.ThrowsAsync<SeatValidationException>(() =>
            manager.CreateAsync(ValidSeat() with { Id = id }));
    }

    [Fact]
    public async Task RequireOwnedSession_RejectsDifferentUser()
    {
        var store = new MemorySeatStore();
        var sessions = new FakeSessions(
        [
            new SeatSession(8, "somebody-else", "PC", "REMOTE", "RDP-Tcp#4", SeatSessionState.Active)
        ]);
        var manager = new SeatManager(store, sessions);
        await manager.CreateAsync(ValidSeat());

        await Assert.ThrowsAsync<SeatValidationException>(() =>
            manager.RequireOwnedSessionAsync("friend", 8));
    }

    [Fact]
    public async Task ConcurrentCreate_AllowsOnlyOneCopyOfSeatId()
    {
        var store = new MemorySeatStore();
        var manager = new SeatManager(store, new FakeSessions([]));
        var attempts = Enumerable.Range(0, 8)
            .Select(index => CaptureExceptionAsync(() =>
                manager.CreateAsync(ValidSeat() with
                {
                    Id = "same-seat",
                    UserName = $"seat-friend-{index}"
                })))
            .ToArray();

        var errors = await Task.WhenAll(attempts);

        Assert.Single(await store.GetAllAsync());
        Assert.Single(errors, error => error is null);
        Assert.Equal(7, errors.Count(error => error is SeatValidationException));
    }

    [Fact]
    public async Task Create_AssignsNonOverlappingSunshinePortFamilies()
    {
        var store = new MemorySeatStore();
        var manager = new SeatManager(store, new FakeSessions([]));

        var first = await manager.CreateAsync(ValidSeat());
        var second = await manager.CreateAsync(ValidSeat() with
        {
            Id = "friend-two",
            UserName = "seat-friend-two"
        });

        Assert.Equal(47989, first.SunshineBasePort);
        Assert.Equal(48089, second.SunshineBasePort);
        Assert.Empty(
            SunshinePorts.FromBasePort(first.SunshineBasePort).AllPorts.Intersect(
                SunshinePorts.FromBasePort(second.SunshineBasePort).AllPorts));
    }

    [Fact]
    public async Task Create_RejectsOverlappingSunshinePortFamily()
    {
        var store = new MemorySeatStore();
        var manager = new SeatManager(store, new FakeSessions([]));
        await manager.CreateAsync(ValidSeat() with { SunshineBasePort = 47989 });

        var exception = await Assert.ThrowsAsync<SeatValidationException>(() =>
            manager.CreateAsync(ValidSeat() with
            {
                Id = "friend-two",
                UserName = "seat-friend-two",
                SunshineBasePort = 47984
            }));

        Assert.Contains("collides", exception.Message, StringComparison.Ordinal);
    }

    private static SeatDefinition ValidSeat() => new()
    {
        Id = "friend",
        DisplayName = "Friend",
        UserName = "seat-friend",
        HostAddress = "gaming-pc"
    };

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class MemorySeatStore : ISeatStore
    {
        private readonly Dictionary<string, SeatDefinition> _seats = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<SeatDefinition>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SeatDefinition>>(_seats.Values.ToArray());

        public Task<SeatDefinition?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_seats.GetValueOrDefault(id));

        public Task UpsertAsync(SeatDefinition seat, CancellationToken cancellationToken = default)
        {
            _seats[seat.Id] = seat;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_seats.Remove(id));
    }

    private sealed class FakeSessions(IReadOnlyList<SeatSession> sessions) : IWindowsSessionService
    {
        public Task<IReadOnlyList<SeatSession>> GetSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sessions);

        public Task DisconnectAsync(int sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task LogoffAsync(int sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
