using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Agent;
using AgentSeat.Core.Models;

namespace AgentSeat.Windows;

public sealed record AgentFileShareOptions(string Root);

/// <summary>
/// The per-seat hand-off folder (<c>&lt;root&gt;\&lt;seat id&gt;</c>). An administrator creates it with an ACL that
/// lets only the owner and the seat account write inside it; the service only ever empties it. The seat account
/// is untrusted and can create links inside the folder, so deletion removes a link as a link and never follows
/// it: following one as SYSTEM would let the seat account delete files anywhere on the machine.
/// </summary>
public sealed class AgentFileShare(AgentFileShareOptions options) : IAgentFileShare
{
    private readonly string _root = Path.GetFullPath(options.Root);

    public string RootFor(SeatDefinition seat)
    {
        // The seat id is validated (^[a-z][a-z0-9-]{0,31}$) when a seat is stored; re-checked here because it
        // becomes part of a path that SYSTEM deletes under.
        _ = AgentPipe.NameFor(seat.Id);
        return Path.Combine(_root, seat.Id);
    }

    public AgentShareClearResult Clear(SeatDefinition seat)
    {
        var folder = new DirectoryInfo(RootFor(seat));
        if (!folder.Exists)
        {
            return new AgentShareClearResult(0, 0);
        }

        if (IsLink(folder))
        {
            throw new InvalidOperationException(
                $"'{folder.FullName}' is a link, not a real folder; refusing to delete through it.");
        }

        var removed = 0;
        var failed = 0;
        DeleteChildren(folder, ref removed, ref failed);
        return new AgentShareClearResult(removed, failed);
    }

    private static void DeleteChildren(DirectoryInfo directory, ref int removed, ref int failed)
    {
        // Hidden and system entries count too (the default enumeration would skip them).
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        FileSystemInfo[] entries;
        try
        {
            entries = directory.GetFileSystemInfos("*", options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failed++;
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (IsLink(entry))
                {
                    // Remove the link itself; its target is never touched.
                    if (entry is DirectoryInfo)
                    {
                        Directory.Delete(entry.FullName, recursive: false);
                    }
                    else
                    {
                        File.Delete(entry.FullName);
                    }

                    removed++;
                }
                else if (entry is DirectoryInfo child)
                {
                    DeleteChildren(child, ref removed, ref failed);
                    child.Attributes = FileAttributes.Normal;
                    child.Delete();
                    removed++;
                }
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                    removed++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++; // In use or protected: leave it and report it rather than abort the whole wipe.
            }
        }
    }

    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
