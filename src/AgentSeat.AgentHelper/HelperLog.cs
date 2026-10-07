namespace AgentSeat.AgentHelper;

/// <summary>A tiny append-only log in the seat user's profile; the helper has no console to write to.</summary>
internal static class HelperLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    internal static void Initialize(string seatId)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agent-seat",
                "Agent");
            _ = Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, $"agent-helper-{seatId}.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            {
                File.Delete(_path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _path = null;
        }
    }

    internal static void Write(string message)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the helper down.
        }
    }
}
