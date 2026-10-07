using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using AgentSeat.Core.Agent;

namespace AgentSeat.AgentHelper;

/// <summary>Read-only, bounded UIA observation inside the seat. A hung provider cannot wedge input.</summary>
internal static class UiObservation
{
    private const int TimeoutMilliseconds = 3500;
    private static readonly JsonSerializerOptions TextJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static JsonObject Capture(AgentRequest request)
    {
        using var worker = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        worker.StartInfo.ArgumentList.Add("--observe-uia");
        try
        {
            worker.Start();
            var output = worker.StandardOutput.ReadToEndAsync();
            var errors = worker.StandardError.ReadToEndAsync();
            worker.StandardInput.WriteLine(JsonSerializer.Serialize(request, AgentJson.Options));
            worker.StandardInput.Close();
            if (!worker.WaitForExit(TimeoutMilliseconds))
            {
                worker.Kill(entireProcessTree: true);
                return Unavailable("UI Automation timed out; use an image.");
            }

            _ = errors.GetAwaiter().GetResult();
            var result = output.GetAwaiter().GetResult();
            return result.Length <= 100000 && JsonNode.Parse(result) is JsonObject data
                ? data : Unavailable("UI Automation returned no usable observation; use an image.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException
                                          or InvalidOperationException or JsonException)
        {
            return Unavailable("UI Automation is unavailable; use an image.");
        }
    }

    internal static int RunWorker()
    {
        // WinExe workers have redirected pipes but no console. Console.InputEncoding calls SetConsoleCP,
        // which fails in a hidden WTS process; use the inherited streams directly instead.
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        JsonObject? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                var request = JsonSerializer.Deserialize<AgentRequest>(input.ReadLine() ?? "{}", AgentJson.Options);
                result = request is null || AgentRequestValidator.Validate(request) is not null
                    ? Unavailable("Invalid observation request.") : Read(request);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                result = Unavailable("UI Automation provider failed; use an image.");
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join(); // The parent kills this process if a provider stalls.
        output.WriteLine((result ?? Unavailable("No observation.")).ToJsonString(AgentJson.Options));
        return 0;
    }

    private static JsonObject Read(AgentRequest request)
    {
        var handle = request.Handle is { } selected ? new IntPtr(selected) : NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
        {
            return Unavailable("No foreground window; use an image.");
        }

        _ = NativeMethods.GetWindowThreadProcessId(handle, out var pid);
        using var target = Process.GetProcessById((int)pid);
        using var self = Process.GetCurrentProcess();
        if (target.SessionId != self.SessionId)
        {
            return Unavailable("The window is outside this seat session.");
        }

        var root = AutomationElement.FromHandle(handle);
        var maxElements = request.MaxElements ?? 60;
        var maxCharacters = request.MaxCharacters ?? 4000;
        var text = new StringBuilder();
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;
        var informative = 0;
        var truncated = false;
        var walker = TreeWalker.ControlViewWalker;
        while (stack.Count > 0 && visited < maxElements && text.Length < maxCharacters)
        {
            var (element, depth) = stack.Pop();
            try
            {
                var current = element.Current;
                if (current.IsOffscreen || current.ControlType == ControlType.TitleBar)
                {
                    continue;
                }

                visited++;
                var role = current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
                var password = current.IsPassword;
                // Some providers expose password content through Name as well as Value/Text.
                var name = password ? "[password]" : Clean(current.Name, 160);
                var bounds = current.BoundingRectangle;
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"[{visited}] {new string(' ', Math.Min(depth, 6))}{role} {JsonSerializer.Serialize(name, TextJson)}");
                if (!bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y))
                {
                    line += string.Create(CultureInfo.InvariantCulture,
                        $" @ {Math.Round(bounds.X)},{Math.Round(bounds.Y)} {Math.Round(bounds.Width)}x{Math.Round(bounds.Height)}");
                }

                if (current.HasKeyboardFocus) line += " focused";
                if (!current.IsEnabled) line += " disabled";
                string? value = null;
                if (!password)
                {
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
                    {
                        value = Clean(((ValuePattern)valuePattern).Current.Value, 350);
                    }
                    else if (current.HasKeyboardFocus && element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
                    {
                        value = Clean(((TextPattern)textPattern).DocumentRange.GetText(700), 700);
                    }
                    if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
                    {
                        line += " toggle=" + ((TogglePattern)toggle).Current.ToggleState;
                    }
                    if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection) &&
                        ((SelectionItemPattern)selection).Current.IsSelected) line += " selected";
                }
                if (!string.IsNullOrEmpty(value)) line += " value=" + JsonSerializer.Serialize(value, TextJson);
                if (role is not ("Window" or "Pane" or "Group" or "Custom") &&
                    (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(value))) informative++;
                var remaining = maxCharacters - text.Length;
                if (line.Length + 1 > remaining)
                {
                    text.Append(line.AsSpan(0, Math.Max(0, remaining)));
                    truncated = true;
                    break;
                }
                text.AppendLine(line);
                if (password || depth >= 12) continue;
                // Bound both traversal and output. Never enumerate the entire desktop tree.
                var children = new List<AutomationElement>();
                var child = walker.GetFirstChild(element);
                while (child is not null && children.Count < maxElements)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }
                truncated |= child is not null;
                for (var index = children.Count - 1; index >= 0; index--) stack.Push((children[index], depth + 1));
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException
                                              or System.Runtime.InteropServices.COMException)
            {
                // A control can disappear during capture; preserve the rest of the observation.
                truncated = true;
            }
        }
        return new JsonObject
        {
            ["available"] = informative > 0,
            ["windowHandle"] = handle.ToInt64(),
            ["text"] = text.ToString().TrimEnd(),
            ["elements"] = visited,
            ["truncated"] = truncated || stack.Count > 0
        };
    }

    private static string Clean(string value, int limit) =>
        new(value.Take(limit).Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private static JsonObject Unavailable(string message) => new() { ["available"] = false, ["text"] = message };
}
