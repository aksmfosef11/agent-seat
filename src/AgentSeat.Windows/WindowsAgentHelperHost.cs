using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Agent;
using AgentSeat.Core.Models;

namespace AgentSeat.Windows;

public sealed record AgentHelperHostOptions(string HelperPath)
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Bound on a liveness check; a frozen helper must not be able to stall attach, start or status.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Starts <c>AgentSeat.AgentHelper</c> inside a seat's WTS session and talks to it over its named
/// pipe. Because the service is usually LocalSystem and the helper runs as a low-privileged seat
/// user, every connection is opened with anonymous impersonation (the helper can never act as the
/// service) and only accepted when the pipe is served by the exact process that was verified.
/// </summary>
public sealed class WindowsAgentHelperHost(
    ISessionProcessLauncher launcher,
    AgentHelperHostOptions options) : IAgentHelperHost
{
    private readonly string _helperPath = Path.GetFullPath(options.HelperPath);

    public async Task<AgentHelperInfo?> TryAttachAsync(
        SeatDefinition seat,
        int sessionId,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in FindHelperProcesses(sessionId))
        {
            try
            {
                var response = await ExchangeAsync(seat, candidate, new AgentRequest { Action = AgentActions.Info }, cancellationToken, options.HandshakeTimeout)
                    .ConfigureAwait(false);
                if (response.Ok)
                {
                    return candidate;
                }
            }
            catch (AgentControlException)
            {
                // Not answering on this seat's pipe: leave it for LaunchAsync to clean up.
            }
        }

        return null;
    }

    public async Task<AgentHelperInfo> LaunchAsync(
        SeatDefinition seat,
        int sessionId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_helperPath))
        {
            throw new AgentControlException(
                AgentControlException.HelperUnavailable,
                $"The agent helper was not found at '{_helperPath}'.");
        }

        // A helper that no longer answers still owns the pipe name; remove it so the new one can bind.
        foreach (var stale in FindHelperProcesses(sessionId))
        {
            _ = Stop(stale);
        }

        var serviceSid = WindowsIdentity.GetCurrent().User?.Value ??
                         throw new AgentControlException(
                             AgentControlException.HelperUnavailable,
                             "Could not determine the service identity.");
        SessionProcessStartResult started;
        try
        {
            started = launcher.Start(
                sessionId,
                _helperPath,
                ["--seat", seat.Id, "--allow-sid", serviceSid],
                Path.GetDirectoryName(_helperPath)!);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException
                                              or InvalidOperationException)
        {
            throw new AgentControlException(
                AgentControlException.HelperUnavailable,
                $"The agent helper could not be started in session {sessionId}: {exception.Message}",
                exception);
        }

        var helper = new AgentHelperInfo(started.ProcessId, sessionId, started.StartedAtUtc);
        var deadline = DateTimeOffset.UtcNow + options.LaunchTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning(helper))
            {
                throw new AgentControlException(
                    AgentControlException.HelperUnavailable,
                    "The agent helper exited right after starting. See agent-helper-<seat>.log in the seat's %LocalAppData%\\agent-seat\\Agent.");
            }

            try
            {
                var response = await ExchangeAsync(seat, helper, new AgentRequest { Action = AgentActions.Info }, cancellationToken, options.HandshakeTimeout)
                    .ConfigureAwait(false);
                if (response.Ok)
                {
                    return helper;
                }
            }
            catch (AgentControlException)
            {
                // The pipe is not up yet; the deadline check below decides when to give up.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _ = Stop(helper);
                throw new AgentControlException(
                    AgentControlException.HelperUnavailable,
                    "The agent helper did not open its pipe in time.");
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<AgentResponse> SendAsync(
        SeatDefinition seat,
        AgentHelperInfo helper,
        AgentRequest request,
        CancellationToken cancellationToken) =>
        ExchangeAsync(seat, helper, request, cancellationToken);

    public bool IsRunning(AgentHelperInfo helper)
    {
        try
        {
            using var process = Process.GetProcessById(helper.ProcessId);
            return !process.HasExited &&
                   Math.Abs((process.StartTime.ToUniversalTime() - helper.StartedUtc.UtcDateTime).TotalSeconds) < 2;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public bool Stop(AgentHelperInfo helper)
    {
        try
        {
            using var process = Process.GetProcessById(helper.ProcessId);
            // Re-verify identity right before killing so a recycled PID is never touched.
            if (process.SessionId != helper.SessionId || !IsHelperImage(process.Id))
            {
                return false;
            }

            process.Kill();
            _ = process.WaitForExit(3000);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public int StopInSession(int sessionId)
    {
        // No handshake: identity is decided by executable path and session alone, so a helper that is frozen,
        // or whose pipe was squatted, is still removed.
        var stopped = 0;
        foreach (var helper in FindHelperProcesses(sessionId))
        {
            if (Stop(helper))
            {
                stopped++;
            }
        }

        return stopped;
    }

    public bool ReleaseInputs(SeatDefinition seat, int sessionId)
    {
        try
        {
            _ = launcher.Start(
                sessionId,
                _helperPath,
                ["--seat", seat.Id, "--release-inputs"],
                Path.GetDirectoryName(_helperPath)!);
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException
                                              or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private async Task<AgentResponse> ExchangeAsync(
        SeatDefinition seat,
        AgentHelperInfo helper,
        AgentRequest request,
        CancellationToken cancellationToken,
        TimeSpan? responseTimeout = null)
    {
        if (!IsRunning(helper))
        {
            // A client waits for a missing pipe to appear; a dead helper's pipe never will.
            throw new AgentControlException(
                AgentControlException.HelperUnavailable,
                "The agent helper process is not running.");
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            AgentPipe.NameFor(seat.Id),
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Anonymous);
        try
        {
            await pipe.ConnectAsync((int)options.ConnectTimeout.TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new AgentControlException(
                AgentControlException.HelperUnavailable,
                $"The agent helper's pipe is not reachable: {exception.Message}",
                exception);
        }

        if (!NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId) ||
            serverProcessId != (uint)helper.ProcessId)
        {
            throw new AgentControlException(
                AgentControlException.HelperUnavailable,
                "The pipe is not served by the verified helper process; refusing to send commands to it.");
        }

        // A frozen helper accepts the connection and never answers; callers that only want to know whether it
        // is alive bound the wait so it cannot stall attach, start, status or the kill switch.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (responseTimeout is { } limit)
        {
            bounded.CancelAfter(limit);
        }

        try
        {
            await AgentFraming.WriteJsonAsync(pipe, request, bounded.Token).ConfigureAwait(false);
            return await AgentFraming.ReadJsonAsync<AgentResponse>(pipe, bounded.Token).ConfigureAwait(false) ??
                   throw new AgentControlException(
                       AgentControlException.HelperFailed,
                       "The agent helper closed the pipe without answering.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentControlException(
                AgentControlException.HelperFailed,
                "The agent helper did not answer in time.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
                                              or System.Text.Json.JsonException)
        {
            throw new AgentControlException(
                AgentControlException.HelperFailed,
                $"The exchange with the agent helper broke: {exception.Message}",
                exception);
        }
    }

    private List<AgentHelperInfo> FindHelperProcesses(int sessionId)
    {
        var found = new List<AgentHelperInfo>();
        foreach (var process in Process.GetProcessesByName(AgentPipe.HelperProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == sessionId && IsHelperImage(process.Id))
                    {
                        found.Add(new AgentHelperInfo(
                            process.Id,
                            sessionId,
                            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)));
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception)
                {
                    // The process exited while it was being inspected.
                }
            }
        }

        return found;
    }

    private bool IsHelperImage(int processId) =>
        NativeMethods.TryGetImagePath(processId) is { } path &&
        string.Equals(Path.GetFullPath(path), _helperPath, StringComparison.OrdinalIgnoreCase);

    private static class NativeMethods
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

        internal static string? TryGetImagePath(int processId)
        {
            using var handle = OpenProcess(ProcessQueryLimitedInformation, inherit: false, processId);
            if (handle.IsInvalid)
            {
                return null;
            }

            var path = new StringBuilder(1024);
            var size = path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref size) ? path.ToString() : null;
        }
    }
}
