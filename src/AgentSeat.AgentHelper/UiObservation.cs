using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using AgentSeat.Core.Agent;

namespace AgentSeat.AgentHelper;

internal sealed record UiWindow(long Handle, uint ProcessId, uint ThreadId, long ProcessStarted);

internal sealed record UiIdentity(UiWindow Window, string RuntimeId, int Type, string Name, string AutomationId);

internal sealed record UiTarget(int Index, bool CanExpand, string[] Actions, UiIdentity Identity);

internal sealed record UiWorkerRequest(AgentRequest Request, UiIdentity? Target);

internal sealed record UiWorkerResult(AgentResponse Response, List<UiTarget>? Targets = null);

/// <summary>Reads and operates on UIA elements in a disposable MTA process inside the seat.</summary>
internal static class UiObservation
{
    private const int MaxScan = 750;
    private static readonly JsonSerializerOptions TextJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static int RunWorker()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        UiWorkerResult? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                var command = JsonSerializer.Deserialize<UiWorkerRequest>(input.ReadLine() ?? "{}", AgentJson.Options);
                result = command?.Request is { } request && AgentRequestValidator.Validate(request) is null
                    ? Execute(request, command.Target)
                    : new(Refusal("invalid_request", "Invalid UI Automation request."));
            }
            catch (AgentActionException exception)
            {
                result = new(Refusal(exception.Code, exception.Message));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                result = new(Refusal("uia_unavailable", "UI Automation could not read the target."));
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        output.WriteLine(JsonSerializer.Serialize(result, AgentJson.Options));
        return 0;
    }

    private static UiWorkerResult Execute(AgentRequest request, UiIdentity? target)
    {
        if (request.Action == AgentActions.WaitFor)
        {
            return new(Probe(request));
        }

        var handle = target?.Window.Handle ?? request.Handle ?? NativeMethods.GetForegroundWindow().ToInt64();
        var window = GetWindow(handle);
        if (window is null || (target is not null && target.Window != window))
        {
            return new(Refusal(target is null ? "window_not_found" : "stale_ref",
                target is null ? "The window is unavailable in this seat." : "The target window changed. Observe again."));
        }

        var root = AutomationElement.FromHandle(new IntPtr(handle));
        if (target is not null)
        {
            var mutation = AgentActions.IsInput(request.Action);
            var (element, failure) = Resolve(root, target, mutation);
            if (element is null)
            {
                return new(failure!);
            }

            if (mutation)
            {
                return new(Mutate(request, element, target));
            }

            root = element;
        }

        return request.Action == AgentActions.Observe
            ? Read(request, window, root)
            : new(Refusal("stale_ref", "Observe the element before acting."));
    }

    private static (AutomationElement?, AgentResponse?) Resolve(AutomationElement root, UiIdentity target, bool mutation)
    {
        var candidates = root.FindAll(TreeScope.Subtree, new AndCondition(
            new PropertyCondition(AutomationElement.IsPasswordProperty, false),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.LookupById(target.Type)),
            new PropertyCondition(AutomationElement.NameProperty, target.Name),
            new PropertyCondition(AutomationElement.AutomationIdProperty, target.AutomationId)));
        if (mutation && candidates.Count > 1)
        {
            return (null, Refusal("ambiguous_target", "Several controls have the same UI Automation identity. Use an explicit coordinate action if needed."));
        }

        foreach (AutomationElement candidate in candidates)
        {
            if (RuntimeId(candidate) == target.RuntimeId)
            {
                return (candidate, null);
            }
        }

        return (null, Refusal("stale_ref", "The element changed or disappeared. Observe again."));
    }

    private static AgentResponse Mutate(AgentRequest request, AutomationElement element, UiIdentity target)
    {
        var current = element.Current;
        var window = new IntPtr(target.Window.Handle);
        if (GetWindow(target.Window.Handle) != target.Window || current.IsPassword ||
            current.Name != target.Name || current.AutomationId != target.AutomationId ||
            current.ControlType.Id != target.Type)
        {
            return Refusal("stale_ref", "The element changed. Observe again.");
        }

        if (!current.IsEnabled || current.IsOffscreen || !NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
        {
            return Refusal("element_unavailable", "The element is disabled or offscreen.");
        }

        if (request.Action == AgentActions.Invoke &&
            element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            return Dispatch(((InvokePattern)invoke).Invoke);
        }

        if (request.Action == AgentActions.SetValue &&
            element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && !((ValuePattern)value).Current.IsReadOnly)
        {
            return Dispatch(() => ((ValuePattern)value).SetValue(request.Text!));
        }

        return Refusal("action_not_supported", "The element does not support this UI Automation action.");
    }

    private static AgentResponse Dispatch(Action action)
    {
        try
        {
            action();
            return AgentResponse.Success() with { Disposition = AgentDisposition.Dispatched };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return AgentResponse.Failure("action_failed", "UI Automation did not confirm dispatch. Observe the UI before acting again.")
                with { Disposition = AgentDisposition.Uncertain };
        }
    }

    private static AgentResponse Probe(AgentRequest request)
    {
        var handle = request.Handle ?? WindowCatalog.FindByTitle(request.WindowTitle!);
        var window = handle is { } found ? GetWindow(found) : null;
        var matched = window is not null && NativeMethods.IsWindowVisible(new IntPtr(window.Handle));
        if (matched && (request.Role is not null || request.Name is not null))
        {
            var conditions = new List<Condition>
            {
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
                new PropertyCondition(AutomationElement.IsPasswordProperty, false)
            };
            if (request.Role is { } role)
            {
                var field = typeof(ControlType).GetField(role, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                if (field?.GetValue(null) is not ControlType controlType)
                {
                    return Refusal("invalid_request", "Use a UI Automation role such as Button, Edit or Text.");
                }
                conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
            }
            if (request.Name is { } name)
            {
                conditions.Add(new PropertyCondition(AutomationElement.NameProperty, name, PropertyConditionFlags.IgnoreCase));
            }
            var root = AutomationElement.FromHandle(new IntPtr(window!.Handle));
            matched = root.FindFirst(TreeScope.Subtree, new AndCondition(conditions.ToArray())) is not null;
        }

        return AgentResponse.Success(new JsonObject { ["matched"] = matched, ["windowHandle"] = window?.Handle });
    }

    private static UiWorkerResult Read(AgentRequest request, UiWindow window, AutomationElement root)
    {
        var maxElements = request.MaxElements ?? 60;
        var maxCharacters = request.MaxCharacters ?? 4000;
        var maxDepth = request.MaxDepth ?? 12;
        var interactive = request.InteractiveOnly == true;
        var scanLimit = interactive ? MaxScan : maxElements;
        var text = new StringBuilder();
        var targets = new List<UiTarget>();
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        var scanned = 0;
        var emitted = 0;
        var informative = 0;
        var truncated = false;
        var walker = TreeWalker.ControlViewWalker;
        while (stack.Count > 0 && scanned < scanLimit && emitted < maxElements && text.Length < maxCharacters)
        {
            var (element, depth) = stack.Pop();
            scanned++;
            try
            {
                var current = element.Current;
                if (current.IsOffscreen || current.ControlType == ControlType.TitleBar)
                {
                    continue;
                }

                var password = current.IsPassword;
                var child = password ? null : walker.GetFirstChild(element);
                var canExpand = child is not null;
                var actions = Actions(element, current);
                if (!interactive || depth == 0 || actions.Length > 0 || current.IsKeyboardFocusable ||
                    (canExpand && (depth == maxDepth || current.Name.Length > 0)))
                {
                    var line = Describe(element, current, depth, emitted + 1, out var useful);
                    var remaining = maxCharacters - text.Length;
                    if (line.Length + Environment.NewLine.Length > remaining)
                    {
                        truncated = true;
                        break;
                    }
                    text.AppendLine(line);
                    emitted++;
                    if (useful || actions.Length > 0) informative++;
                    if ((canExpand || actions.Length > 0) && window.ProcessStarted != 0 &&
                        current.Name.Length <= 256 && current.AutomationId.Length <= 256 &&
                        RuntimeId(element) is { Length: > 0 and <= 256 } id)
                    {
                        targets.Add(new(emitted, canExpand, actions,
                            new(window, id, current.ControlType.Id, current.Name, current.AutomationId)));
                    }
                }

                if (depth == maxDepth)
                {
                    truncated |= canExpand;
                    continue;
                }

                // Limit work even when filtering skips most of the rows in a dense tree.
                var children = new List<AutomationElement>();
                while (child is not null && children.Count < scanLimit - scanned)
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
                truncated = true;
            }
        }

        return new(AgentResponse.Success(new JsonObject
        {
            ["available"] = informative > 0,
            ["windowHandle"] = window.Handle,
            ["text"] = text.ToString().TrimEnd(),
            ["elements"] = emitted,
            ["truncated"] = truncated || stack.Count > 0
        }), targets);
    }

    private static string[] Actions(AutomationElement element, AutomationElement.AutomationElementInformation current)
    {
        if (current.IsPassword || !current.IsEnabled) return [];
        var actions = new List<string>(2);
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out _)) actions.Add(AgentActions.Invoke);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && !((ValuePattern)value).Current.IsReadOnly)
        {
            actions.Add(AgentActions.SetValue);
        }
        return actions.ToArray();
    }

    private static string Describe(
        AutomationElement element, AutomationElement.AutomationElementInformation current, int depth, int index, out bool useful)
    {
        var role = current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
        var name = current.IsPassword ? "[password]" : Clean(current.Name, 160);
        var bounds = current.BoundingRectangle;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"[{index}] {new string(' ', Math.Min(depth, 6))}{role} {JsonSerializer.Serialize(name, TextJson)}");
        if (!bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y))
        {
            line += string.Create(CultureInfo.InvariantCulture,
                $" @ {Math.Round(bounds.X)},{Math.Round(bounds.Y)} {Math.Round(bounds.Width)}x{Math.Round(bounds.Height)}");
        }

        if (current.HasKeyboardFocus) line += " focused";
        if (!current.IsEnabled) line += " disabled";
        string? value = null;
        if (!current.IsPassword)
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
        useful = role is not ("Window" or "Pane" or "Group" or "Custom") && (name.Length > 0 || !string.IsNullOrEmpty(value));
        return line;
    }

    private static UiWindow? GetWindow(long handle)
    {
        var hwnd = new IntPtr(handle);
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return null;
        var thread = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (thread == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            using var self = Process.GetCurrentProcess();
            if (process.SessionId != self.SessionId) return null;
            long started;
            try { started = process.StartTime.ToUniversalTime().Ticks; }
            catch (Win32Exception) { started = 0; } // A read is still useful when process identity cannot be proven.
            return new(handle, pid, thread, started);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string RuntimeId(AutomationElement element) => string.Join(',', element.GetRuntimeId());

    private static string Clean(string value, int limit) =>
        new(value.Take(limit).Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    internal static AgentResponse Refusal(string code, string message) =>
        AgentResponse.Failure(code, message) with { Disposition = AgentDisposition.NotDelivered };
}
