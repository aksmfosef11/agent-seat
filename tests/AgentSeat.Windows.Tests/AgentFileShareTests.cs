using System.Diagnostics;
using AgentSeat.Core.Agent;
using AgentSeat.Core.Models;
using AgentSeat.Windows;

namespace AgentSeat.Windows.Tests;

/// <summary>
/// The service (SYSTEM) empties a folder that an untrusted seat account can write into. If it ever followed a
/// link the seat account planted there, that account could make SYSTEM delete files anywhere on the machine.
/// </summary>
public sealed class AgentFileShareTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "agent-seat-share-" + Guid.NewGuid().ToString("N"));

    public AgentFileShareTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        // A leftover junction must be removed as a link, never recursed into, or the test would delete its own target.
        foreach (var entry in new DirectoryInfo(_base).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry is DirectoryInfo)
            {
                Directory.Delete(entry.FullName, recursive: false);
            }
        }

        Directory.Delete(_base, recursive: true);
    }

    private static SeatDefinition Seat(string id = "agent") => new()
    {
        Id = id,
        DisplayName = id,
        UserName = "seat-agent",
        HostAddress = "pc",
        AgentControlEnabled = true
    };

    private string Root => Path.Combine(_base, "share");

    private AgentFileShare NewShare() => new(new AgentFileShareOptions(Root));

    private string SeatFolder()
    {
        var path = Path.Combine(Root, "agent");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void MakeJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "could not create a test junction");
    }

    [Fact]
    public void ClearRemovesFilesAndSubfoldersButKeepsTheFolderItself()
    {
        var folder = SeatFolder();
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(folder, "sub", "deeper"));
        File.WriteAllText(Path.Combine(folder, "sub", "deeper", "b.txt"), "b");

        var result = NewShare().Clear(Seat());

        Assert.Equal(new AgentShareClearResult(4, 0), result); // a.txt, sub\deeper\b.txt, deeper, sub
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void ReadOnlyAndHiddenFilesAreRemovedToo()
    {
        var folder = SeatFolder();
        var locked = Path.Combine(folder, "locked.txt");
        File.WriteAllText(locked, "x");
        File.SetAttributes(locked, FileAttributes.ReadOnly | FileAttributes.Hidden);

        var result = NewShare().Clear(Seat());

        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Failed);
        Assert.False(File.Exists(locked));
    }

    [Fact]
    public void AJunctionPlantedInsideTheFolderIsRemovedWithoutTouchingItsTarget()
    {
        var folder = SeatFolder();
        var outside = Path.Combine(_base, "precious");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "do-not-delete.txt");
        File.WriteAllText(sentinel, "keep me");
        MakeJunction(Path.Combine(folder, "innocent-looking"), outside);
        File.WriteAllText(Path.Combine(folder, "normal.txt"), "n");

        var result = NewShare().Clear(Seat());

        Assert.Equal(0, result.Failed);
        Assert.Equal(2, result.Removed); // the junction (as a link) and normal.txt
        Assert.Empty(Directory.GetFileSystemEntries(folder));
        Assert.True(File.Exists(sentinel), "the junction was followed and its target was deleted");
        Assert.Equal("keep me", File.ReadAllText(sentinel));
    }

    [Fact]
    public void AJunctionNestedInASubfolderIsAlsoNotFollowed()
    {
        var folder = SeatFolder();
        var outside = Path.Combine(_base, "precious");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "do-not-delete.txt");
        File.WriteAllText(sentinel, "keep me");
        Directory.CreateDirectory(Path.Combine(folder, "work"));
        MakeJunction(Path.Combine(folder, "work", "link"), outside);

        _ = NewShare().Clear(Seat());

        Assert.True(File.Exists(sentinel));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void AShareFolderThatWasSwappedForALinkIsRefused()
    {
        Directory.CreateDirectory(Root);
        var outside = Path.Combine(_base, "precious");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "do-not-delete.txt");
        File.WriteAllText(sentinel, "keep me");
        MakeJunction(Path.Combine(Root, "agent"), outside);

        Assert.Throws<InvalidOperationException>(() => NewShare().Clear(Seat()));

        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void ANonExistentFolderHasNothingToClear()
    {
        Assert.Equal(new AgentShareClearResult(0, 0), NewShare().Clear(Seat()));
    }

    [Fact]
    public void AFileInUseIsReportedAndTheRestIsStillWiped()
    {
        var folder = SeatFolder();
        var busy = Path.Combine(folder, "busy.txt");
        File.WriteAllText(busy, "x");
        File.WriteAllText(Path.Combine(folder, "free.txt"), "y");

        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = NewShare().Clear(Seat());

            Assert.Equal(1, result.Failed);
            Assert.Equal(1, result.Removed);
        }

        Assert.True(File.Exists(busy));
        Assert.False(File.Exists(Path.Combine(folder, "free.txt")));
    }

    [Theory]
    [InlineData(@"..\evil")]
    [InlineData("a/b")]
    [InlineData("Upper")]
    [InlineData("")]
    public void TheSeatIdUsedInThePathIsValidatedAgain(string seatId)
    {
        Assert.Throws<ArgumentException>(() => NewShare().RootFor(Seat(seatId)));
    }

    [Fact]
    public void TheFolderLivesDirectlyUnderTheConfiguredRoot()
    {
        Assert.Equal(Path.Combine(Root, "agent"), NewShare().RootFor(Seat()));
    }
}
