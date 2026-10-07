using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentSeat.Windows;

public sealed record SessionProcessStartResult(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string UserProfilePath);

public interface ISessionProcessLauncher
{
    string GetUserProfilePath(int sessionId);

    SessionProcessStartResult Start(
        int sessionId,
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory);
}

/// <summary>
/// Starts a process on the interactive desktop belonging to a WTS session. Launching into a
/// different user's session requires AgentSeat to run as LocalSystem (the installed service mode).
/// </summary>
public sealed class SessionProcessLauncher : ISessionProcessLauncher
{
    public string GetUserProfilePath(int sessionId)
    {
        ValidateSessionId(sessionId);
        if (Process.GetCurrentProcess().SessionId == sessionId)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        using var token = QuerySessionToken(sessionId);
        uint length = 0;
        _ = NativeMethods.GetUserProfileDirectory(token, null, ref length);
        var firstError = Marshal.GetLastWin32Error();
        if (length == 0 && firstError != NativeMethods.ErrorInsufficientBuffer)
        {
            throw new Win32Exception(firstError, $"Could not size the profile path for session {sessionId}.");
        }

        var profile = new StringBuilder((int)length);
        if (!NativeMethods.GetUserProfileDirectory(token, profile, ref length))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not query the user profile for session {sessionId}.");
        }

        return Path.GetFullPath(profile.ToString());
    }

    public SessionProcessStartResult Start(
        int sessionId,
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        ValidateSessionId(sessionId);
        executablePath = Path.GetFullPath(executablePath);
        workingDirectory = Path.GetFullPath(workingDirectory);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The session executable was not found.", executablePath);
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory '{workingDirectory}' was not found.");
        }

        if (Process.GetCurrentProcess().SessionId == sessionId)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo) ??
                          throw new InvalidOperationException("Windows did not return a process for Sunshine.");
            using (process)
            {
                return new SessionProcessStartResult(
                    process.Id,
                    new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
        }

        using var sessionToken = QuerySessionToken(sessionId);
        if (!NativeMethods.DuplicateTokenEx(
                sessionToken,
                NativeMethods.MaximumAllowed,
                IntPtr.Zero,
                NativeMethods.SecurityImpersonation,
                NativeMethods.TokenPrimary,
                out var primaryToken))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not duplicate the user token for session {sessionId}.");
        }

        using (primaryToken)
        {
            if (!NativeMethods.CreateEnvironmentBlock(out var environment, primaryToken, inherit: false))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not create the user environment for session {sessionId}.");
            }

            try
            {
                var startup = new NativeMethods.StartupInfo
                {
                    Size = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                    Desktop = @"winsta0\default",
                    Flags = NativeMethods.StartfUseShowWindow,
                    ShowWindow = NativeMethods.SwHide
                };
                var commandLine = new StringBuilder(BuildCommandLine(executablePath, arguments));
                var created = NativeMethods.CreateProcessAsUser(
                    primaryToken,
                    executablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateNoWindow,
                    environment,
                    workingDirectory,
                    ref startup,
                    out var processInformation);
                if (!created)
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(
                        error,
                        error == NativeMethods.ErrorPrivilegeNotHeld
                            ? "Launching Sunshine in another WTS session requires the installed LocalSystem AgentSeat service."
                            : $"Could not launch Sunshine in Windows session {sessionId}.");
                }

                try
                {
                    using var process = Process.GetProcessById(processInformation.ProcessId);
                    return new SessionProcessStartResult(
                        processInformation.ProcessId,
                        new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                        GetUserProfilePath(sessionId));
                }
                catch
                {
                    _ = NativeMethods.TerminateProcess(
                        processInformation.ProcessHandle,
                        NativeMethods.ErrorProcessAborted);
                    throw;
                }
                finally
                {
                    if (processInformation.ProcessHandle != IntPtr.Zero)
                    {
                        _ = NativeMethods.CloseHandle(processInformation.ProcessHandle);
                    }
                    if (processInformation.ThreadHandle != IntPtr.Zero)
                    {
                        _ = NativeMethods.CloseHandle(processInformation.ThreadHandle);
                    }
                }
            }
            finally
            {
                _ = NativeMethods.DestroyEnvironmentBlock(environment);
            }
        }
    }

    private static SafeAccessTokenHandle QuerySessionToken(int sessionId)
    {
        if (!NativeMethods.WTSQueryUserToken((uint)sessionId, out var token))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                error == NativeMethods.ErrorPrivilegeNotHeld
                    ? "WTSQueryUserToken requires AgentSeat to run as LocalSystem. Install the Windows service before starting a friend's stream."
                    : $"Could not obtain the user token for Windows session {sessionId}.");
        }

        return token;
    }

    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var values = new[] { executablePath }.Concat(arguments);
        return string.Join(' ', values.Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        var output = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                output.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            output.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        output.Append('\\', backslashes * 2).Append('"');
        return output.ToString();
    }

    private static void ValidateSessionId(int sessionId)
    {
        if (sessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }
    }

    private static class NativeMethods
    {
        internal const uint MaximumAllowed = 0x02000000;
        internal const int SecurityImpersonation = 2;
        internal const int TokenPrimary = 1;
        internal const uint CreateUnicodeEnvironment = 0x00000400;
        internal const uint CreateNoWindow = 0x08000000;
        internal const int StartfUseShowWindow = 0x00000001;
        internal const short SwHide = 0;
        internal const int ErrorInsufficientBuffer = 122;
        internal const int ErrorPrivilegeNotHeld = 1314;
        internal const uint ErrorProcessAborted = 1067;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal int Size;
            internal string? Reserved;
            internal string? Desktop;
            internal string? Title;
            internal int X;
            internal int Y;
            internal int XSize;
            internal int YSize;
            internal int XCountChars;
            internal int YCountChars;
            internal int FillAttribute;
            internal int Flags;
            internal short ShowWindow;
            internal short Reserved2;
            internal IntPtr ReservedPointer;
            internal IntPtr StandardInput;
            internal IntPtr StandardOutput;
            internal IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            internal IntPtr ProcessHandle;
            internal IntPtr ThreadHandle;
            internal int ProcessId;
            internal int ThreadId;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateTokenEx(
            SafeAccessTokenHandle existingToken,
            uint desiredAccess,
            IntPtr tokenAttributes,
            int impersonationLevel,
            int tokenType,
            out SafeAccessTokenHandle newToken);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserProfileDirectory(
            SafeAccessTokenHandle token,
            StringBuilder? profileDirectory,
            ref uint size);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateEnvironmentBlock(
            out IntPtr environment,
            SafeAccessTokenHandle token,
            [MarshalAs(UnmanagedType.Bool)] bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessAsUser(
            SafeAccessTokenHandle token,
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);
    }
}
