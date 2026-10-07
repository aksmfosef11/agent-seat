using System.Reflection;
using System.Runtime.InteropServices;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;

namespace AgentSeat.Windows;

public sealed class RdpAnchorScheduledTaskService : ISeatAnchorService
{
    private const string TaskPrefix = "agent-seat RDP Anchor - ";
    private const int FileNotFoundHResult = unchecked((int)0x80070002);
    private const int TaskNotRunningHResult = unchecked((int)0x8004130B);

    public Task StartAsync(SeatDefinition seat, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var task = ScheduledTask.Open(TaskName(seat), required: true)!;

        object? runningTask = null;
        try
        {
            task.SetEnabled(true);
            runningTask = task.Run();

            // The task has an at-logon trigger for installation/bootstrap. Disable it again as
            // soon as this demand-start instance exists so a reboot or later logon cannot bring
            // the seat back without another click in the management UI.
            task.SetEnabled(false);
        }
        catch
        {
            try
            {
                task.SetEnabled(false);
            }
            catch (Exception exception) when (exception is COMException or TargetInvocationException)
            {
                // Preserve the original start failure.
            }

            throw;
        }
        finally
        {
            Release(runningTask);
        }

        return Task.CompletedTask;
    }

    public Task<bool> StopAsync(SeatDefinition seat, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var task = ScheduledTask.Open(TaskName(seat), required: false);
        if (task is null)
        {
            return Task.FromResult(false);
        }

        // Disable before stopping so the task's restart-on-failure policy cannot race the logoff.
        task.SetEnabled(false);
        try
        {
            task.Stop();
        }
        catch (Exception exception) when (ComHResult(exception) == TaskNotRunningHResult)
        {
            // An already-stopped anchor is the requested end state.
        }

        return Task.FromResult(true);
    }

    private static string TaskName(SeatDefinition seat) => $"\\{TaskPrefix}{seat.Id}";

    private static int ComHResult(Exception exception)
    {
        return exception is TargetInvocationException { InnerException: { } inner }
            ? inner.HResult
            : exception.HResult;
    }

    private static object? Invoke(object target, string member, BindingFlags flags, object?[]? arguments = null) =>
        target.GetType().InvokeMember(member, flags, null, target, arguments);

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private sealed class ScheduledTask : IDisposable
    {
        private readonly object _scheduler;
        private readonly object _folder;
        private readonly object _task;

        private ScheduledTask(object scheduler, object folder, object task)
        {
            _scheduler = scheduler;
            _folder = folder;
            _task = task;
        }

        internal static ScheduledTask? Open(string taskName, bool required)
        {
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service") ??
                                throw new PlatformNotSupportedException("Windows Task Scheduler is unavailable.");
            var scheduler = Activator.CreateInstance(schedulerType) ??
                            throw new InvalidOperationException("Could not connect to Windows Task Scheduler.");
            object? folder = null;
            object? task = null;
            try
            {
                _ = Invoke(scheduler, "Connect", BindingFlags.InvokeMethod);
                folder = Invoke(scheduler, "GetFolder", BindingFlags.InvokeMethod, ["\\"]) ??
                         throw new InvalidOperationException("Could not open the Task Scheduler root folder.");
                try
                {
                    task = Invoke(folder, "GetTask", BindingFlags.InvokeMethod, [taskName]);
                }
                catch (Exception exception) when (ComHResult(exception) == FileNotFoundHResult)
                {
                    if (!required)
                    {
                        return null;
                    }

                    throw new KeyNotFoundException(
                        $"The managed RDP anchor task '{taskName}' was not found.", exception);
                }

                if (task is null)
                {
                    throw new InvalidOperationException($"Task Scheduler returned no task for '{taskName}'.");
                }

                var result = new ScheduledTask(scheduler, folder, task);
                scheduler = null!;
                folder = null;
                task = null;
                return result;
            }
            finally
            {
                Release(task);
                Release(folder);
                Release(scheduler);
            }
        }

        internal void SetEnabled(bool enabled) =>
            _ = Invoke(_task, "Enabled", BindingFlags.SetProperty, [enabled]);

        internal object? Run() =>
            Invoke(_task, "Run", BindingFlags.InvokeMethod, [null]);

        internal void Stop() =>
            _ = Invoke(_task, "Stop", BindingFlags.InvokeMethod, [0]);

        public void Dispose()
        {
            Release(_task);
            Release(_folder);
            Release(_scheduler);
        }
    }
}
