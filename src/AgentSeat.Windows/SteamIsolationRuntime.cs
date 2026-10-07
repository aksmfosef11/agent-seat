namespace AgentSeat.Windows;

internal static class SteamIsolationRuntime
{
    private static readonly string[] Architectures = ["win-x86", "win-x64"];
    private static readonly string[] NativeFiles =
        ["AgentSeat.Injector.exe", "AgentSeat.AppCompat.dll"];

    internal static bool IsComplete(string launcherPath, string compatibilityDirectory) =>
        File.Exists(Path.GetFullPath(launcherPath)) &&
        Architectures.All(architecture =>
            NativeFiles.All(file =>
                File.Exists(Path.Combine(
                    Path.GetFullPath(compatibilityDirectory),
                    architecture,
                    file))));
}
