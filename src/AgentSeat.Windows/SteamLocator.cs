using Microsoft.Win32;

namespace AgentSeat.Windows;

internal static class SteamLocator
{
    private const string SteamRegistryKey = @"SOFTWARE\Valve\Steam";
    private static ReadOnlySpan<byte> MasterIpcOverrideOption => "master_ipc_name_override"u8;

    internal static string? FindExecutable()
    {
        var candidates = new List<string>();
        AddProgramFilesCandidate(candidates, Environment.SpecialFolder.ProgramFilesX86);
        AddProgramFilesCandidate(candidates, Environment.SpecialFolder.ProgramFiles);
        AddRegistryCandidate(candidates, RegistryView.Registry32);
        AddRegistryCandidate(candidates, RegistryView.Registry64);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // Ignore malformed third-party registry values and continue through known locations.
            }
        }

        return null;
    }

    internal static bool SupportsMasterIpcOverride(string executablePath)
    {
        try
        {
            return File.ReadAllBytes(executablePath).AsSpan().IndexOf(MasterIpcOverrideOption) >= 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void AddProgramFilesCandidate(
        ICollection<string> candidates,
        Environment.SpecialFolder folder)
    {
        var root = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(root))
        {
            candidates.Add(Path.Combine(root, "Steam", "steam.exe"));
        }
    }

    private static void AddRegistryCandidate(ICollection<string> candidates, RegistryView view)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var steam = machine.OpenSubKey(SteamRegistryKey, writable: false);
        var installationDirectory = steam?.GetValue(
            "InstallPath",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        if (!string.IsNullOrWhiteSpace(installationDirectory))
        {
            candidates.Add(Path.Combine(installationDirectory, "steam.exe"));
        }
    }
}
