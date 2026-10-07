using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentSeat.Core.Agent;

namespace AgentSeat.AgentHelper;

/// <summary>Lists and manages the top-level windows of this session.</summary>
internal static class WindowCatalog
{
    private const int MaxWindows = 100;

    internal static JsonObject List()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var windows = new JsonArray();
        NativeMethods.EnumWindowsProc callback = (window, _) =>
        {
            if (windows.Count >= MaxWindows || !IsUserWindow(window))
            {
                return windows.Count < MaxWindows;
            }

            windows.Add(Describe(window, foreground));
            return true;
        };
        _ = NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return new JsonObject { ["windows"] = windows };
    }

    internal static JsonObject Foreground()
    {
        var window = NativeMethods.GetForegroundWindow();
        return window == IntPtr.Zero ? new JsonObject() : Describe(window, window);
    }

    internal static JsonObject Focus(long handle)
    {
        var window = Resolve(handle);
        if (NativeMethods.IsIconic(window))
        {
            _ = NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        }

        // Windows only lets the foreground thread hand the foreground to someone else, so borrow its input queue.
        var current = NativeMethods.GetCurrentThreadId();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var attached = foregroundThread != 0 && foregroundThread != current &&
                       NativeMethods.AttachThreadInput(current, foregroundThread, attach: true);
        try
        {
            _ = NativeMethods.BringWindowToTop(window);
            _ = NativeMethods.SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                _ = NativeMethods.AttachThreadInput(current, foregroundThread, attach: false);
            }
        }

        Thread.Sleep(80);
        if (NativeMethods.GetForegroundWindow() != window)
        {
            // Windows refuses foreground changes from a process that was not just given input. A tap of Alt
            // counts as input and lifts that lock, so retry once after it.
            InputInjector.PressKeys("alt");
            _ = NativeMethods.BringWindowToTop(window);
            _ = NativeMethods.SetForegroundWindow(window);
            Thread.Sleep(80);
        }

        return new JsonObject { ["focused"] = NativeMethods.GetForegroundWindow() == window };
    }

    internal static void Close(long handle)
    {
        var window = Resolve(handle);
        if (!NativeMethods.PostMessageW(window, NativeMethods.WmClose, UIntPtr.Zero, IntPtr.Zero))
        {
            throw new AgentActionException("action_failed", "Windows refused to close the window.");
        }
    }

    internal static JsonObject Launch(AgentRequest request)
    {
        var startInfo = new ProcessStartInfo(request.Path!)
        {
            // ShellExecute so that documents and URLs open with their default app, not only executables.
            UseShellExecute = true,
            Arguments = request.Arguments is { Length: > 0 } arguments ? AgentPipe.JoinArguments(arguments) : string.Empty
        };
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        try
        {
            using var process = Process.Start(startInfo);
            return new JsonObject { ["processId"] = process?.Id ?? 0 };
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException
                                              or FileNotFoundException)
        {
            throw new AgentActionException("action_failed", $"Could not launch '{request.Path}': {exception.Message}", exception);
        }
    }

    private static IntPtr Resolve(long handle)
    {
        var window = new IntPtr(handle);
        if (!NativeMethods.IsWindow(window))
        {
            throw new AgentActionException("window_not_found", $"Window {handle} no longer exists.");
        }

        return window;
    }

    private static bool IsUserWindow(IntPtr window)
    {
        if (!NativeMethods.IsWindowVisible(window) || NativeMethods.GetWindowTextLengthW(window) == 0)
        {
            return false;
        }

        if ((NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExToolWindow) != 0)
        {
            return false;
        }

        return NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DwmaCloaked, out var cloaked, sizeof(int)) != 0 ||
               cloaked == 0;
    }

    private static JsonObject Describe(IntPtr window, IntPtr foreground)
    {
        var title = new StringBuilder(NativeMethods.GetWindowTextLengthW(window) + 1);
        _ = NativeMethods.GetWindowTextW(window, title, title.Capacity);
        _ = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        _ = NativeMethods.GetWindowRect(window, out var rect);
        return new JsonObject
        {
            ["handle"] = window.ToInt64(),
            ["title"] = title.ToString(),
            ["processId"] = (int)processId,
            ["processName"] = ProcessName(processId),
            ["x"] = rect.Left,
            ["y"] = rect.Top,
            ["width"] = rect.Right - rect.Left,
            ["height"] = rect.Bottom - rect.Top,
            ["minimized"] = NativeMethods.IsIconic(window),
            ["foreground"] = window == foreground
        };
    }

    private static string ProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
