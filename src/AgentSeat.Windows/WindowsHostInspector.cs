using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;

namespace AgentSeat.Windows;

public sealed class WindowsHostInspector(SunshineStreamingOptions streamingOptions) : IHostInspector
{
    private const string TerminalServerKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string RdpTcpKey = TerminalServerKey + @"\WinStations\RDP-Tcp";
    private const string TermServiceParametersKey =
        @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public Task<PreflightReport> InspectAsync(
        IReadOnlyCollection<SeatDefinition> seats,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checks = new List<PreflightCheck>();

        var productName = ReadString(CurrentVersionKey, "ProductName") ?? "Windows";
        var displayVersion = ReadString(CurrentVersionKey, "DisplayVersion");
        var build = ReadString(CurrentVersionKey, "CurrentBuildNumber");
        var installationType = ReadString(CurrentVersionKey, "InstallationType") ?? string.Empty;
        var operatingSystem = string.Join(' ', new[] { productName, displayVersion, build }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var serverEdition = installationType.Contains("Server", StringComparison.OrdinalIgnoreCase) ||
                            productName.Contains("Server", StringComparison.OrdinalIgnoreCase);

        checks.Add(new PreflightCheck(
            "windows",
            "Windows host",
            PreflightStatus.Pass,
            operatingSystem));

        var sunshineExecutable = Path.Combine(
            Path.GetFullPath(streamingOptions.TemplateDirectory),
            "sunshine.exe");
        var sunshineApps = Path.Combine(
            Path.GetFullPath(streamingOptions.TemplateDirectory),
            "assets",
            "apps.json");
        var sunshineReady = File.Exists(sunshineExecutable) && File.Exists(sunshineApps);
        checks.Add(new PreflightCheck(
            "sunshine_template",
            "Verified Sunshine portable",
            sunshineReady ? PreflightStatus.Pass : PreflightStatus.Fail,
            sunshineReady
                ? $"Sunshine template found at '{streamingOptions.TemplateDirectory}'."
                : $"Sunshine portable template is incomplete at '{streamingOptions.TemplateDirectory}'.",
            sunshineReady ? null : "Run scripts/Get-Sunshine.ps1, then restart AgentSeat."));

        var steamExecutable = SteamLocator.FindExecutable();
        var steamIpcReady = steamExecutable is not null &&
                            SteamLocator.SupportsMasterIpcOverride(steamExecutable);
        checks.Add(new PreflightCheck(
            "steam_host",
            "Steam host application",
            steamIpcReady ? PreflightStatus.Pass : PreflightStatus.Fail,
            steamExecutable is null
                ? "Steam is not installed. The default Moonlight Steam entry cannot start."
                : steamIpcReady
                    ? $"Steam at '{steamExecutable}' can seed a separate per-seat runtime and supports a separate master IPC name. AgentSeat stores no Steam credentials."
                    : $"Steam is installed at '{steamExecutable}', but this build does not expose the required master IPC override.",
            steamIpcReady
                ? null
                : "Install or update the official Steam client. Do not enter a Steam username or password into AgentSeat."));

        var isolationReady = SteamIsolationRuntime.IsComplete(
            streamingOptions.SteamLauncherPath,
            streamingOptions.AppCompatDirectory);
        checks.Add(new PreflightCheck(
            "steam_isolation",
            "Per-seat Steam AppCompat isolation",
            isolationReady ? PreflightStatus.Pass : PreflightStatus.Fail,
            isolationReady
                ? $"The managed Steam launcher and x86/x64 compatibility runtimes are ready at '{streamingOptions.AppCompatDirectory}'."
                : $"The Steam launcher or native compatibility files are incomplete. Launcher: '{streamingOptions.SteamLauncherPath}', runtime: '{streamingOptions.AppCompatDirectory}'.",
            isolationReady
                ? null
                : "Run scripts/Build-AppCompat.ps1, then scripts/Publish.ps1 and restart AgentSeat."));

        var serviceIdentity = WindowsIdentity.GetCurrent().IsSystem;
        checks.Add(new PreflightCheck(
            "session_launcher_identity",
            "Cross-session process launcher",
            serviceIdentity ? PreflightStatus.Pass : PreflightStatus.Warning,
            serviceIdentity
                ? "AgentSeat runs as LocalSystem and can launch Sunshine with a friend's WTS user token."
                : "AgentSeat can launch Sunshine only in its own current session in this console mode.",
            serviceIdentity ? null : "Publish and install AgentSeat as its LocalSystem Windows service before using a friend's RDP session."));

        using var vigemKey = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\ViGEmBus");
        var vigemInstalled = vigemKey is not null;
        checks.Add(new PreflightCheck(
            "gamepad_driver",
            "Virtual gamepad support",
            vigemInstalled ? PreflightStatus.Pass : PreflightStatus.Warning,
            vigemInstalled
                ? "ViGEmBus is installed; Moonlight controllers can be exposed to games."
                : "ViGEmBus is not installed. Video, keyboard, and mouse still work, but Moonlight gamepads do not.",
            vigemInstalled ? null : "Review and run scripts/Install-GamepadSupport.ps1 -Apply if controller support is required."));

        var administrator = IsAdministrator();
        checks.Add(new PreflightCheck(
            "administrator",
            "Administrator token",
            administrator ? PreflightStatus.Pass : PreflightStatus.Warning,
            administrator
                ? "AgentSeat is running with an elevated token."
                : "Read-only management works, but setup and session logoff may fail without elevation.",
            administrator ? null : "Run the service or console once as Administrator for host setup."));

        var rdpEnabled = ReadInt32(TerminalServerKey, "fDenyTSConnections") == 0;
        checks.Add(new PreflightCheck(
            "rdp_enabled",
            "Remote Desktop listener enabled",
            rdpEnabled ? PreflightStatus.Pass : PreflightStatus.Fail,
            rdpEnabled
                ? "Windows accepts Remote Desktop connections."
                : "Remote Desktop is disabled in Windows.",
            rdpEnabled ? null : "Enable Remote Desktop or run scripts/Install-MultiSession.ps1 after reviewing it."));

        var serviceState = QueryServiceState("TermService");
        var serviceRunning = serviceState == ServiceState.Running;
        checks.Add(new PreflightCheck(
            "termservice",
            "Remote Desktop Services",
            serviceRunning ? PreflightStatus.Pass : PreflightStatus.Fail,
            $"TermService state: {serviceState}.",
            serviceRunning ? null : "Start the Remote Desktop Services (TermService) service."));

        var rdpPort = ReadInt32(RdpTcpKey, "PortNumber") ?? 3389;
        var listening = IsTcpPortListening(rdpPort);
        checks.Add(new PreflightCheck(
            "rdp_port",
            $"RDP TCP port {rdpPort}",
            listening ? PreflightStatus.Pass : PreflightStatus.Warning,
            listening
                ? "A local TCP listener is active."
                : "No local TCP listener was observed yet.",
            listening ? null : "Check TermService, the RDP listener, and Windows Firewall."));

        var serviceDll = ReadString(TermServiceParametersKey, "ServiceDll") ?? string.Empty;
        var termWrapActive = Path.GetFileName(serviceDll)
            .Equals("TermWrap.dll", StringComparison.OrdinalIgnoreCase);
        if (serverEdition)
        {
            checks.Add(new PreflightCheck(
                "multi_session_policy",
                "Concurrent-session policy",
                PreflightStatus.Warning,
                "Windows Server was detected. Concurrent interactive use can require the RDS role and valid RDS CALs.",
                "Confirm Microsoft licensing and configure Remote Desktop Services for your deployment."));
        }
        else if (termWrapActive)
        {
            checks.Add(new PreflightCheck(
                "multi_session_policy",
                "Concurrent-session policy",
                PreflightStatus.Pass,
                $"TermService currently loads '{serviceDll}'. This is an unsupported Windows client modification; updates may break it."));
        }
        else
        {
            checks.Add(new PreflightCheck(
                "multi_session_policy",
                "Concurrent-session policy",
                PreflightStatus.Fail,
                "Stock Windows client normally switches or locks the console when an RDP user signs in; it does not provide Duo-style simultaneous seats.",
                "Use a properly licensed Windows Server/RDS host, or explicitly review and install the optional MIT TermWrap dependency."));
        }

        if (seats.Count == 0)
        {
            checks.Add(new PreflightCheck(
                "seat_accounts",
                "Dedicated seat accounts",
                PreflightStatus.Warning,
                "No seats are configured.",
                "Create one dedicated local Windows account per remote friend, then add a seat."));
        }
        else
        {
            foreach (var seat in seats.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exists = AccountExists(seat.UserName);
                var remoteDesktopMember = exists && IsRemoteDesktopUser(seat.UserName);
                var status = exists && remoteDesktopMember ? PreflightStatus.Pass : PreflightStatus.Fail;
                var detail = !exists
                    ? $"Windows account '{seat.UserName}' could not be resolved."
                    : remoteDesktopMember
                        ? $"Windows account '{seat.UserName}' resolves and belongs to Remote Desktop Users."
                        : $"Windows account '{seat.UserName}' exists but is not in Remote Desktop Users.";
                checks.Add(new PreflightCheck(
                    $"account_{seat.Id}",
                    $"Account for {seat.DisplayName}",
                    status,
                    detail,
                    status == PreflightStatus.Pass
                        ? null
                        : "Create the account if needed and add it to the Remote Desktop Users local group."));
            }
        }

        checks.RemoveAll(check => check.Code is "sunshine_template" or "steam_host" or "steam_isolation" or "gamepad_driver");
        return Task.FromResult(new PreflightReport(
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            operatingSystem,
            checks));
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool AccountExists(string configuredName)
    {
        try
        {
            NTAccount account;
            if (configuredName.Contains('\\', StringComparison.Ordinal) ||
                configuredName.Contains('@', StringComparison.Ordinal))
            {
                account = new NTAccount(configuredName);
            }
            else
            {
                account = new NTAccount(Environment.MachineName, configuredName);
            }

            _ = account.Translate(typeof(SecurityIdentifier));
            return true;
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
        catch (SystemException)
        {
            return false;
        }
    }

    private static bool IsRemoteDesktopUser(string configuredName)
    {
        var lookupName = configuredName;
        var slash = configuredName.IndexOf('\\');
        if (slash > 0)
        {
            var authority = configuredName[..slash];
            if (authority == "." || authority.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                lookupName = configuredName[(slash + 1)..];
            }
        }

        var result = NativeMethods.NetUserGetLocalGroups(
            null,
            lookupName,
            0,
            NativeMethods.IncludeIndirectGroups,
            out var buffer,
            NativeMethods.MaxPreferredLength,
            out var entriesRead,
            out _);
        if (result != 0)
        {
            return false;
        }

        try
        {
            var remoteDesktopUsersSid = new SecurityIdentifier("S-1-5-32-555");
            var translated = (NTAccount)remoteDesktopUsersSid.Translate(typeof(NTAccount));
            var expectedName = translated.Value[(translated.Value.LastIndexOf('\\') + 1)..];
            var itemSize = Marshal.SizeOf<NativeMethods.LocalGroupUsersInfo>();
            for (var index = 0; index < entriesRead; index++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.LocalGroupUsersInfo>(
                    IntPtr.Add(buffer, index * itemSize));
                var groupName = Marshal.PtrToStringUni(item.Name);
                if (string.Equals(groupName, expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NativeMethods.NetApiBufferFree(buffer);
            }
        }
    }

    private static string? ReadString(string keyPath, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
    }

    private static int? ReadInt32(string keyPath, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
        var value = key?.GetValue(valueName);
        return value is null ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsTcpPortListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static ServiceState QueryServiceState(string serviceName)
    {
        var manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return ServiceState.Unknown;
        }

        try
        {
            var service = NativeMethods.OpenService(manager, serviceName, NativeMethods.ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return ServiceState.NotFound;
            }

            try
            {
                var status = new NativeMethods.ServiceStatusProcess();
                return NativeMethods.QueryServiceStatusEx(
                    service,
                    0,
                    ref status,
                    Marshal.SizeOf<NativeMethods.ServiceStatusProcess>(),
                    out _)
                    ? (ServiceState)status.CurrentState
                    : ServiceState.Unknown;
            }
            finally
            {
                _ = NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = NativeMethods.CloseServiceHandle(manager);
        }
    }

    private enum ServiceState : uint
    {
        Unknown = 0,
        Stopped = 1,
        StartPending = 2,
        StopPending = 3,
        Running = 4,
        ContinuePending = 5,
        PausePending = 6,
        Paused = 7,
        NotFound = 8
    }

    private static class NativeMethods
    {
        internal const uint ScManagerConnect = 0x0001;
        internal const uint ServiceQueryStatus = 0x0004;
        internal const int IncludeIndirectGroups = 0x0001;
        internal const int MaxPreferredLength = -1;

        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatusProcess
        {
            internal uint ServiceType;
            internal uint CurrentState;
            internal uint ControlsAccepted;
            internal uint Win32ExitCode;
            internal uint ServiceSpecificExitCode;
            internal uint CheckPoint;
            internal uint WaitHint;
            internal uint ProcessId;
            internal uint ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct LocalGroupUsersInfo
        {
            internal readonly IntPtr Name;
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(
            IntPtr service,
            int infoLevel,
            ref ServiceStatusProcess status,
            int bufferSize,
            out int bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("netapi32.dll", EntryPoint = "NetUserGetLocalGroups", CharSet = CharSet.Unicode)]
        internal static extern int NetUserGetLocalGroups(
            string? serverName,
            string userName,
            int level,
            int flags,
            out IntPtr buffer,
            int preferredMaximumLength,
            out int entriesRead,
            out int totalEntries);

        [DllImport("netapi32.dll")]
        internal static extern int NetApiBufferFree(IntPtr buffer);
    }
}
