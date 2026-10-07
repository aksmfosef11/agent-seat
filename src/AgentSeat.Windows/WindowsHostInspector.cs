using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;

namespace AgentSeat.Windows;

public sealed class WindowsHostInspector : IHostInspector
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
        var serverEdition = installationType.Contains("Server", StringComparison.OrdinalIgnoreCase) ||
                            productName.Contains("Server", StringComparison.OrdinalIgnoreCase);
        // Client ProductName can retain "Windows 10" after an upgrade; report the edition and build directly.
        var hostName = serverEdition ? productName : $"Windows {ReadString(CurrentVersionKey, "EditionID")}";
        var operatingSystem = string.Join(' ', new[] { hostName, displayVersion, build is null ? null : $"(build {build})" }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

        checks.Add(new PreflightCheck(
            "windows",
            "Windows host",
            PreflightStatus.Pass,
            operatingSystem));

        var serviceIdentity = WindowsIdentity.GetCurrent().IsSystem;
        checks.Add(new PreflightCheck(
            "session_launcher_identity",
            "Seat helper service",
            serviceIdentity ? PreflightStatus.Pass : PreflightStatus.Warning,
            serviceIdentity
                ? "agent-seat can start its screen and input helper in the dedicated seat."
                : "The service must be installed to start a helper in another Windows session.",
            serviceIdentity ? null : "Install agent-seat with Install.ps1 before using its dedicated desktop."));

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
                "Run Install.ps1 to create a dedicated local Windows account and AI seat."));
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
