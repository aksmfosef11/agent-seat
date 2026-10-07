using AgentSeat.Core.Models;
using AgentSeat.Core.Storage;

namespace AgentSeat.Core.Tests;

public sealed class JsonSeatStoreTests
{
    [Fact]
    public async Task Store_RoundTripsAndDeletesSeat()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSeat.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "seats.json");

        try
        {
            using (var store = new JsonSeatStore(file))
            {
                await store.UpsertAsync(new SeatDefinition
                {
                    Id = "friend",
                    DisplayName = "Friend",
                    UserName = "seat-friend",
                    HostAddress = "gaming-pc"
                });
            }

            using (var reopened = new JsonSeatStore(file))
            {
                var seat = Assert.Single(await reopened.GetAllAsync());
                Assert.Equal("seat-friend", seat.UserName);
                Assert.True(await reopened.DeleteAsync("FRIEND"));
                Assert.Empty(await reopened.GetAllAsync());
            }
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            Directory.Delete(directory, recursive: false);
        }
    }

    [Fact]
    public async Task Store_MigratesVersionOneSeatsToUniqueSunshinePorts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSeat.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "legacy.json");

        try
        {
            await File.WriteAllTextAsync(file, """
                {
                  "schemaVersion": 1,
                  "seats": [
                    { "id": "a", "displayName": "A", "userName": "seat-a", "hostAddress": "host" },
                    { "id": "b", "displayName": "B", "userName": "seat-b", "hostAddress": "host" }
                  ]
                }
                """);

            using var store = new JsonSeatStore(file);
            var seats = await store.GetAllAsync();

            Assert.Equal([47989, 48089], seats.Select(seat => seat.SunshineBasePort));
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(directory, recursive: false);
        }
    }
}
