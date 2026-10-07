using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentSeat.Windows;

public sealed record VirtualGamepadIsolationResult(
    int Updated,
    int Restarted,
    int RestartPending,
    IReadOnlyList<string> Failures);

/// <summary>
/// ViGEmBus creates Moonlight controllers as system-wide PnP devices, so XInput, DirectInput,
/// HID readers and Steam Input in every WTS session can open them. This stores a device security
/// descriptor on each ViGEm-enumerated device instance that grants access only to LocalSystem and
/// the seat users. Windows applies a stored descriptor whenever it builds the device stack, so a
/// controller that reappears with the same instance ID is protected before any process opens it.
/// Kernel-mode readers (raw input, services running as LocalSystem) are not restricted.
/// </summary>
public sealed class VirtualGamepadIsolation
{
    private const string BusServiceName = "ViGEmBus";
    private const int GenericAll = 0x10000000;
    private const uint FileDeviceSecureOpen = 0x00000100;

    private readonly HashSet<string> _verified = new(StringComparer.OrdinalIgnoreCase);
    private string? _verifiedDescriptor;

    public static string BuildSecurityDescriptor(IEnumerable<SecurityIdentifier> seatUsers)
    {
        ArgumentNullException.ThrowIfNull(seatUsers);
        var descriptor = new StringBuilder("D:P(A;;GA;;;SY)");
        foreach (var sid in seatUsers
                     .Select(user => user.Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            descriptor.Append("(A;;GA;;;").Append(sid).Append(')');
        }

        return descriptor.ToString();
    }

    /// <summary>
    /// Recognizes descriptors produced by <see cref="BuildSecurityDescriptor"/> so that rollback
    /// never removes a descriptor another tool placed on the device.
    /// </summary>
    public static bool IsIsolationDescriptor(string? securityDescriptor)
    {
        if (string.IsNullOrWhiteSpace(securityDescriptor))
        {
            return false;
        }

        RawSecurityDescriptor descriptor;
        try
        {
            descriptor = new RawSecurityDescriptor(securityDescriptor);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
            descriptor.DiscretionaryAcl is not { Count: > 0 } acl)
        {
            return false;
        }

        var aces = acl.Cast<GenericAce>().ToList();
        return aces.All(ace => ace is CommonAce
               {
                   AceType: AceType.AccessAllowed,
                   AccessMask: GenericAll,
                   AceFlags: AceFlags.None
               }) &&
               aces.OfType<CommonAce>().Any(
                   ace => ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid));
    }

    /// <summary>
    /// Applies <paramref name="securityDescriptor"/> to connected ViGEm controllers and restarts
    /// any that were already running without it. With <paramref name="includeDisconnected"/>, the
    /// descriptor is also stored on remembered instances so their next arrival is protected at once.
    /// </summary>
    public VirtualGamepadIsolationResult Apply(string securityDescriptor, bool includeDisconnected)
    {
        var desired = Normalize(securityDescriptor) ??
                      throw new ArgumentException("The security descriptor is not valid SDDL.", nameof(securityDescriptor));
        if (!string.Equals(_verifiedDescriptor, desired, StringComparison.Ordinal))
        {
            _verified.Clear();
            _verifiedDescriptor = desired;
        }

        var devices = includeDisconnected ? FindAllDescendants() : FindConnectedDescendants();
        var updated = new List<ControllerDevice>();
        var failures = new List<string>();
        foreach (var device in devices)
        {
            if (!_verified.Add(device.InstanceId))
            {
                continue;
            }

            try
            {
                if (EnsureDescriptor(device.InstanceId, securityDescriptor, desired))
                {
                    updated.Add(device);
                }
            }
            catch (Win32Exception exception)
            {
                // Retry on the next pass; a controller may disappear while it is being inspected.
                _verified.Remove(device.InstanceId);
                failures.Add(exception.Message);
            }
        }

        var (restarted, pending) = RestartTopmost(updated, failures);
        return new VirtualGamepadIsolationResult(updated.Count, restarted, pending, failures);
    }

    /// <summary>
    /// Removes AgentSeat descriptors from every ViGEm instance and restarts connected controllers
    /// so that they return to the driver's default, system-wide visibility.
    /// </summary>
    public VirtualGamepadIsolationResult Clear()
    {
        _verified.Clear();
        _verifiedDescriptor = null;
        var updated = new List<ControllerDevice>();
        var failures = new List<string>();
        foreach (var device in FindAllDescendants())
        {
            try
            {
                using var set = OpenDevice(device.InstanceId, out var data);
                if (set is null ||
                    !IsIsolationDescriptor(ReadStringProperty(set, ref data, NativeMethods.SpdrpSecuritySds)))
                {
                    continue;
                }

                SetProperty(set, ref data, NativeMethods.SpdrpSecuritySds, null);
                if (ReadDwordProperty(set, ref data, NativeMethods.SpdrpCharacteristics) == FileDeviceSecureOpen)
                {
                    SetProperty(set, ref data, NativeMethods.SpdrpCharacteristics, null);
                }
                updated.Add(device);
            }
            catch (Win32Exception exception)
            {
                failures.Add(exception.Message);
            }
        }

        var (restarted, pending) = RestartTopmost(updated, failures);
        return new VirtualGamepadIsolationResult(updated.Count, restarted, pending, failures);
    }

    private static bool EnsureDescriptor(string instanceId, string securityDescriptor, string normalized)
    {
        using var set = OpenDevice(instanceId, out var data);
        if (set is null)
        {
            return false;
        }

        var characteristics = ReadDwordProperty(set, ref data, NativeMethods.SpdrpCharacteristics) ?? 0;
        if (string.Equals(
                Normalize(ReadStringProperty(set, ref data, NativeMethods.SpdrpSecuritySds)),
                normalized,
                StringComparison.Ordinal) &&
            (characteristics & FileDeviceSecureOpen) != 0)
        {
            return false;
        }

        SetProperty(
            set,
            ref data,
            NativeMethods.SpdrpSecuritySds,
            Encoding.Unicode.GetBytes(securityDescriptor + '\0'));
        // Enforce the descriptor even when a reader opens a path below the device object.
        SetProperty(
            set,
            ref data,
            NativeMethods.SpdrpCharacteristics,
            BitConverter.GetBytes(characteristics | FileDeviceSecureOpen));
        return true;
    }

    private static (int Restarted, int Pending) RestartTopmost(
        IReadOnlyList<ControllerDevice> updated,
        List<string> failures)
    {
        var updatedIds = updated.Select(device => device.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restarted = 0;
        var pending = 0;
        foreach (var device in updated.Where(device => device.Connected))
        {
            // Restarting a controller rebuilds its HID children, which read their own stored descriptor.
            if (device.Ancestors.Any(updatedIds.Contains))
            {
                continue;
            }

            try
            {
                if (Restart(device.InstanceId))
                {
                    restarted++;
                }
                else
                {
                    pending++;
                }
            }
            catch (Win32Exception exception)
            {
                pending++;
                failures.Add(exception.Message);
            }
        }

        return (restarted, pending);
    }

    private static bool Restart(string instanceId)
    {
        using var set = OpenDevice(instanceId, out var data);
        if (set is null)
        {
            return true;
        }

        var parameters = new NativeMethods.SpPropChangeParams
        {
            ClassInstallHeader = new NativeMethods.SpClassInstallHeader
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.SpClassInstallHeader>(),
                InstallFunction = NativeMethods.DifPropertyChange
            },
            StateChange = NativeMethods.DicsPropChange,
            Scope = NativeMethods.DicsFlagGlobal
        };
        if (!NativeMethods.SetupDiSetClassInstallParams(
                set,
                ref data,
                ref parameters,
                (uint)Marshal.SizeOf<NativeMethods.SpPropChangeParams>()) ||
            !NativeMethods.SetupDiCallClassInstaller(NativeMethods.DifPropertyChange, set, ref data))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not restart virtual controller '{instanceId}' to apply its security descriptor.");
        }

        var installParameters = new NativeMethods.SpDevInstallParams
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.SpDevInstallParams>()
        };
        return !NativeMethods.SetupDiGetDeviceInstallParams(set, ref data, ref installParameters) ||
               (installParameters.Flags & (NativeMethods.DiNeedRestart | NativeMethods.DiNeedReboot)) == 0;
    }

    private static List<ControllerDevice> FindConnectedDescendants()
    {
        var devices = new List<ControllerDevice>();
        foreach (var bus in GetConnectedBusIds())
        {
            if (NativeMethods.CM_Locate_DevNode(out var node, bus, 0) == NativeMethods.CrSuccess)
            {
                AddChildren(node, [bus], devices);
            }
        }

        return devices;
    }

    private static void AddChildren(uint parent, string[] ancestors, List<ControllerDevice> devices)
    {
        if (NativeMethods.CM_Get_Child(out var child, parent, 0) != NativeMethods.CrSuccess)
        {
            return;
        }

        do
        {
            var instanceId = GetDeviceId(child);
            if (instanceId is null)
            {
                continue;
            }

            devices.Add(new ControllerDevice(instanceId, ancestors, Connected: true));
            AddChildren(child, [.. ancestors, instanceId], devices);
        }
        while (NativeMethods.CM_Get_Sibling(out child, child, 0) == NativeMethods.CrSuccess);
    }

    private static List<ControllerDevice> FindAllDescendants()
    {
        var parents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var buses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var set = NativeMethods.SetupDiGetClassDevs(
                   IntPtr.Zero,
                   null,
                   IntPtr.Zero,
                   NativeMethods.DigcfAllClasses))
        {
            if (set.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate device instances.");
            }

            var data = NewDeviceInfoData();
            for (uint index = 0; NativeMethods.SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var instanceId = ReadInstanceId(set, ref data);
                if (instanceId is null)
                {
                    continue;
                }

                parents[instanceId] = ReadParent(set, ref data);
                if (string.Equals(
                        ReadStringProperty(set, ref data, NativeMethods.SpdrpService),
                        BusServiceName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    buses.Add(instanceId);
                }
            }
        }

        var connected = FindConnectedDescendants()
            .Select(device => device.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = new List<ControllerDevice>();
        foreach (var instanceId in parents.Keys)
        {
            var ancestors = new List<string>();
            var current = parents[instanceId];
            // ViGEm pads sit one level below the bus and their HID collections at most three.
            while (current is not null && ancestors.Count < 4)
            {
                ancestors.Insert(0, current);
                if (buses.Contains(current))
                {
                    devices.Add(new ControllerDevice(
                        instanceId,
                        [.. ancestors],
                        connected.Contains(instanceId)));
                    break;
                }

                current = parents.GetValueOrDefault(current);
            }
        }

        return devices;
    }

    private static List<string> GetConnectedBusIds()
    {
        const uint flags = NativeMethods.CmGetIdListFilterService | NativeMethods.CmGetIdListFilterPresent;
        if (NativeMethods.CM_Get_Device_ID_List_Size(out var length, BusServiceName, flags) != NativeMethods.CrSuccess ||
            length <= 1)
        {
            return [];
        }

        var buffer = new char[length];
        if (NativeMethods.CM_Get_Device_ID_List(BusServiceName, buffer, length, flags) != NativeMethods.CrSuccess)
        {
            return [];
        }

        return new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static string? GetDeviceId(uint node)
    {
        if (NativeMethods.CM_Get_Device_ID_Size(out var length, node, 0) != NativeMethods.CrSuccess)
        {
            return null;
        }

        var buffer = new char[length + 1];
        return NativeMethods.CM_Get_Device_ID(node, buffer, (uint)buffer.Length, 0) == NativeMethods.CrSuccess
            ? new string(buffer, 0, (int)length)
            : null;
    }

    private static SafeDeviceInfoSetHandle? OpenDevice(string instanceId, out NativeMethods.SpDevInfoData data)
    {
        data = NewDeviceInfoData();
        var set = NativeMethods.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a device information set.");
        }

        if (NativeMethods.SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref data))
        {
            return set;
        }

        var error = Marshal.GetLastWin32Error();
        set.Dispose();
        if (error == NativeMethods.ErrorNoSuchDevinst)
        {
            return null;
        }

        throw new Win32Exception(error, $"Could not open virtual controller '{instanceId}'.");
    }

    private static NativeMethods.SpDevInfoData NewDeviceInfoData() =>
        new() { Size = (uint)Marshal.SizeOf<NativeMethods.SpDevInfoData>() };

    private static string? ReadInstanceId(SafeDeviceInfoSetHandle set, ref NativeMethods.SpDevInfoData data)
    {
        var buffer = new char[NativeMethods.MaxDeviceIdLength];
        return NativeMethods.SetupDiGetDeviceInstanceId(set, ref data, buffer, (uint)buffer.Length, out var required)
            ? new string(buffer, 0, (int)Math.Max(0, required - 1))
            : null;
    }

    private static string? ReadParent(SafeDeviceInfoSetHandle set, ref NativeMethods.SpDevInfoData data)
    {
        var key = NativeMethods.DevpkeyDeviceParent;
        var buffer = new byte[(NativeMethods.MaxDeviceIdLength + 1) * sizeof(char)];
        if (!NativeMethods.SetupDiGetDeviceProperty(
                set,
                ref data,
                ref key,
                out var type,
                buffer,
                (uint)buffer.Length,
                out var required,
                0) ||
            type != NativeMethods.DevpropTypeString)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer, 0, (int)required).TrimEnd('\0');
    }

    private static string? ReadStringProperty(SafeDeviceInfoSetHandle set, ref NativeMethods.SpDevInfoData data, uint property)
    {
        var buffer = ReadProperty(set, ref data, property);
        return buffer is null ? null : Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static uint? ReadDwordProperty(SafeDeviceInfoSetHandle set, ref NativeMethods.SpDevInfoData data, uint property)
    {
        var buffer = ReadProperty(set, ref data, property);
        return buffer is { Length: >= sizeof(uint) } ? BitConverter.ToUInt32(buffer, 0) : null;
    }

    private static byte[]? ReadProperty(SafeDeviceInfoSetHandle set, ref NativeMethods.SpDevInfoData data, uint property)
    {
        _ = NativeMethods.SetupDiGetDeviceRegistryProperty(
            set, ref data, property, out _, null, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (error != NativeMethods.ErrorInsufficientBuffer || required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        return NativeMethods.SetupDiGetDeviceRegistryProperty(
                set, ref data, property, out _, buffer, required, out _)
            ? buffer
            : null;
    }

    private static void SetProperty(
        SafeDeviceInfoSetHandle set,
        ref NativeMethods.SpDevInfoData data,
        uint property,
        byte[]? value)
    {
        if (!NativeMethods.SetupDiSetDeviceRegistryProperty(
                set, ref data, property, value, (uint)(value?.Length ?? 0)))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not update device property {property} of a virtual controller.");
        }
    }

    private static string? Normalize(string? securityDescriptor)
    {
        if (string.IsNullOrWhiteSpace(securityDescriptor))
        {
            return null;
        }

        try
        {
            return new RawSecurityDescriptor(securityDescriptor).GetSddlForm(AccessControlSections.All);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed record ControllerDevice(string InstanceId, IReadOnlyList<string> Ancestors, bool Connected);

    private sealed class SafeDeviceInfoSetHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeDeviceInfoSetHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.SetupDiDestroyDeviceInfoList(handle);
    }

    private static class NativeMethods
    {
        internal const uint CrSuccess = 0;
        internal const uint CmGetIdListFilterService = 0x00000002;
        internal const uint CmGetIdListFilterPresent = 0x00000100;
        internal const uint DigcfAllClasses = 0x00000004;
        internal const uint SpdrpService = 0x00000004;
        internal const uint SpdrpSecuritySds = 0x00000018;
        internal const uint SpdrpCharacteristics = 0x0000001B;
        internal const uint DifPropertyChange = 0x00000012;
        internal const uint DicsPropChange = 0x00000003;
        internal const uint DicsFlagGlobal = 0x00000001;
        internal const uint DiNeedRestart = 0x00000080;
        internal const uint DiNeedReboot = 0x00000100;
        internal const uint DevpropTypeString = 0x00000012;
        internal const int ErrorInsufficientBuffer = 122;
        internal const int ErrorNoSuchDevinst = unchecked((int)0xE000020B);
        internal const int MaxDeviceIdLength = 200;

        internal static readonly DevPropKey DevpkeyDeviceParent = new()
        {
            FormatId = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"),
            PropertyId = 8
        };

        [StructLayout(LayoutKind.Sequential)]
        internal struct SpDevInfoData
        {
            internal uint Size;
            internal Guid ClassGuid;
            internal uint DevInst;
            internal IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SpClassInstallHeader
        {
            internal uint Size;
            internal uint InstallFunction;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SpPropChangeParams
        {
            internal SpClassInstallHeader ClassInstallHeader;
            internal uint StateChange;
            internal uint Scope;
            internal uint HardwareProfile;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SpDevInstallParams
        {
            internal uint Size;
            internal uint Flags;
            internal uint FlagsEx;
            internal IntPtr ParentWindow;
            internal IntPtr InstallMessageHandler;
            internal IntPtr InstallMessageHandlerContext;
            internal IntPtr FileQueue;
            internal UIntPtr ClassInstallReserved;
            internal uint Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string DriverPath;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DevPropKey
        {
            internal Guid FormatId;
            internal uint PropertyId;
        }

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", CharSet = CharSet.Unicode)]
        internal static extern uint CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", CharSet = CharSet.Unicode)]
        internal static extern uint CM_Get_Device_ID_List(string filter, [Out] char[] buffer, uint length, uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
        internal static extern uint CM_Locate_DevNode(out uint node, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        internal static extern uint CM_Get_Child(out uint child, uint node, uint flags);

        [DllImport("cfgmgr32.dll")]
        internal static extern uint CM_Get_Sibling(out uint sibling, uint node, uint flags);

        [DllImport("cfgmgr32.dll")]
        internal static extern uint CM_Get_Device_ID_Size(out uint length, uint node, uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
        internal static extern uint CM_Get_Device_ID(uint node, [Out] char[] buffer, uint length, uint flags);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeDeviceInfoSetHandle SetupDiGetClassDevs(
            IntPtr classGuid,
            string? enumerator,
            IntPtr parentWindow,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern SafeDeviceInfoSetHandle SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parentWindow);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInfo(SafeDeviceInfoSetHandle set, uint index, ref SpDevInfoData data);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiOpenDeviceInfo(
            SafeDeviceInfoSetHandle set,
            string instanceId,
            IntPtr parentWindow,
            uint flags,
            ref SpDevInfoData data);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInstanceId(
            SafeDeviceInfoSetHandle set,
            ref SpDevInfoData data,
            [Out] char[] buffer,
            uint length,
            out uint required);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceProperty(
            SafeDeviceInfoSetHandle set,
            ref SpDevInfoData data,
            ref DevPropKey key,
            out uint type,
            [Out] byte[] buffer,
            uint length,
            out uint required,
            uint flags);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceRegistryProperty(
            SafeDeviceInfoSetHandle set,
            ref SpDevInfoData data,
            uint property,
            out uint registryType,
            [Out] byte[]? buffer,
            uint length,
            out uint required);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiSetDeviceRegistryPropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiSetDeviceRegistryProperty(
            SafeDeviceInfoSetHandle set,
            ref SpDevInfoData data,
            uint property,
            byte[]? buffer,
            uint length);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiSetClassInstallParamsW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiSetClassInstallParams(
            SafeDeviceInfoSetHandle set,
            ref SpDevInfoData data,
            ref SpPropChangeParams parameters,
            uint size);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiCallClassInstaller(
            uint installFunction,
            SafeDeviceInfoSetHandle set,
            ref SpDevInfoData data);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstallParamsW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInstallParams(
            SafeDeviceInfoSetHandle set,
            ref SpDevInfoData data,
            ref SpDevInstallParams parameters);
    }
}
