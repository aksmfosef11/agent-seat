using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;

namespace AgentSeat.Windows;

public sealed class WtsSessionService : IWindowsSessionService
{
    public Task<IReadOnlyList<SeatSession>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.WTSEnumerateSessions(
                IntPtr.Zero,
                0,
                1,
                out var buffer,
                out var count))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate Windows sessions.");
        }

        try
        {
            var sessions = new List<SeatSession>(count);
            var itemSize = Marshal.SizeOf<NativeMethods.WtsSessionInfo>();
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pointer = IntPtr.Add(buffer, index * itemSize);
                var native = Marshal.PtrToStructure<NativeMethods.WtsSessionInfo>(pointer);
                sessions.Add(new SeatSession(
                    native.SessionId,
                    QueryString(native.SessionId, NativeMethods.WtsInfoClass.UserName),
                    QueryString(native.SessionId, NativeMethods.WtsInfoClass.DomainName),
                    QueryString(native.SessionId, NativeMethods.WtsInfoClass.ClientName),
                    Marshal.PtrToStringUni(native.StationName) ?? string.Empty,
                    MapState(native.State)));
            }

            return Task.FromResult<IReadOnlyList<SeatSession>>(sessions);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    public Task DisconnectAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSessionId(sessionId);
        if (!NativeMethods.WTSDisconnectSession(IntPtr.Zero, sessionId, wait: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not disconnect session {sessionId}.");
        }

        return Task.CompletedTask;
    }

    public Task LogoffAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSessionId(sessionId);
        if (!NativeMethods.WTSLogoffSession(IntPtr.Zero, sessionId, wait: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not log off session {sessionId}.");
        }

        return Task.CompletedTask;
    }

    private static string QueryString(int sessionId, NativeMethods.WtsInfoClass infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformation(
                IntPtr.Zero,
                sessionId,
                infoClass,
                out var buffer,
                out _))
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    private static SeatSessionState MapState(NativeMethods.WtsConnectState state) => state switch
    {
        NativeMethods.WtsConnectState.Active => SeatSessionState.Active,
        NativeMethods.WtsConnectState.Connected => SeatSessionState.Connected,
        NativeMethods.WtsConnectState.ConnectQuery => SeatSessionState.ConnectQuery,
        NativeMethods.WtsConnectState.Shadow => SeatSessionState.Shadow,
        NativeMethods.WtsConnectState.Disconnected => SeatSessionState.Disconnected,
        NativeMethods.WtsConnectState.Idle => SeatSessionState.Idle,
        NativeMethods.WtsConnectState.Listen => SeatSessionState.Listening,
        NativeMethods.WtsConnectState.Reset => SeatSessionState.Reset,
        NativeMethods.WtsConnectState.Down => SeatSessionState.Down,
        NativeMethods.WtsConnectState.Initializing => SeatSessionState.Initializing,
        _ => SeatSessionState.Unknown
    };

    private static void ValidateSessionId(int sessionId)
    {
        if (sessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }
    }

    private static class NativeMethods
    {
        internal enum WtsConnectState
        {
            Active,
            Connected,
            ConnectQuery,
            Shadow,
            Disconnected,
            Idle,
            Listen,
            Reset,
            Down,
            Initializing
        }

        internal enum WtsInfoClass
        {
            InitialProgram,
            ApplicationName,
            WorkingDirectory,
            OemId,
            SessionId,
            UserName,
            WinStationName,
            DomainName,
            ConnectState,
            ClientBuildNumber,
            ClientName
        }

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct WtsSessionInfo
        {
            internal readonly int SessionId;
            internal readonly IntPtr StationName;
            internal readonly WtsConnectState State;
        }

        [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSEnumerateSessions(
            IntPtr server,
            int reserved,
            int version,
            out IntPtr sessionInfo,
            out int count);

        [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformation(
            IntPtr server,
            int sessionId,
            WtsInfoClass infoClass,
            out IntPtr buffer,
            out int bytesReturned);

        [DllImport("wtsapi32.dll")]
        internal static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSDisconnectSession(IntPtr server, int sessionId, bool wait);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSLogoffSession(IntPtr server, int sessionId, bool wait);
    }
}
