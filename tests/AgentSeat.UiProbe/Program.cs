using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using AgentSeat.AgentHelper;
using AgentSeat.Core.Agent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 2 or > 3) return;
        var ui = new UiAutomation(Path.GetFullPath(args[0]));
        var reportPath = args[1];
        var keepOpenMs = args.Length == 3 && int.TryParse(args[2], out var delay) ? Math.Clamp(delay, 0, 60000) : 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var input = new TextBox { Text = "hello 안녕하세요" };
        AutomationProperties.SetName(input, "Probe input");
        var password = new PasswordBox { Password = "uia-probe-secret-should-never-leak" };
        AutomationProperties.SetName(password, "Probe password");
        var checkbox = new CheckBox { Content = "Probe checked", IsChecked = true };
        var status = new TextBlock { Text = "Pending" };
        var button = new Button { Content = "Probe invoke" };
        var invokes = 0;
        button.Click += (_, _) =>
        {
            invokes++;
            status.Text = "Probe complete";
        };
        var panel = new StackPanel();
        foreach (var child in new FrameworkElement[] { input, password, checkbox, button, status })
        {
            panel.Children.Add(child);
        }
        for (var index = 0; index < 2; index++)
        {
            var duplicate = new Button { Content = "Probe duplicate" };
            AutomationProperties.SetAutomationId(duplicate, "duplicate-id");
            duplicate.Click += (_, _) => invokes++;
            panel.Children.Add(duplicate);
        }
        var group = new GroupBox { Header = "Probe group", Content = panel, Margin = new Thickness(15) };
        var layout = new StackPanel();
        layout.Children.Add(group);
        layout.Children.Add(new Button { Content = "Outside group" });
        var form = new Window
        {
            Title = "AgentSeat UIA verification",
            Width = 520, Height = 320,
            Content = layout
        };
        form.Loaded += async (_, _) =>
        {
            var report = new JsonObject();
            try
            {
                var handle = new WindowInteropHelper(form).Handle.ToInt64();
                input.Focus();
                await Task.Delay(250);
                var observation = new AgentRequest { Action = AgentActions.Observe, Handle = handle };
                var observed = (await Run(observation)).Data!;
                var text = observed["text"]?.GetValue<string>() ?? "";
                report["sessionId"] = Process.GetCurrentProcess().SessionId;
                report["available"] = observed["available"]?.DeepClone();
                report["inputValueRead"] = text.Contains("hello 안녕하세요", StringComparison.Ordinal);
                report["focusRead"] = text.Contains("focused", StringComparison.Ordinal);
                report["toggleRead"] = text.Contains("toggle=On", StringComparison.Ordinal);
                report["passwordHidden"] = !observed.ToJsonString().Contains(password.Password, StringComparison.Ordinal) &&
                    text.Contains("[password]", StringComparison.Ordinal);
                report["stableRefs"] = JsonNode.DeepEquals(observed["targets"], (await Run(observation)).Data!["targets"]);
                var bounded = (await Run(observation with { MaxElements = 1, MaxCharacters = 256 })).Data!;
                report["elementLimitWorks"] = bounded["elements"]?.GetValue<int>() == 1 && bounded["truncated"]?.GetValue<bool>() == true;
                report["characterLimitWorks"] = (bounded["text"]?.GetValue<string>().Length ?? 0) <= 256;
                var overview = (await Run(observation with { InteractiveOnly = true, MaxDepth = 1 })).Data!;
                var groupRef = RefFor(overview, "Group \"Probe group\"");
                var drill = (await Run(new AgentRequest { Action = AgentActions.Observe, RootRef = groupRef, InteractiveOnly = true })).Data!;
                report["drillWorks"] = overview["truncated"]?.GetValue<bool>() == true &&
                    !overview["text"]!.GetValue<string>().Contains("Probe invoke", StringComparison.Ordinal) &&
                    drill["text"]!.GetValue<string>().Contains("Probe invoke", StringComparison.Ordinal) &&
                    !drill["text"]!.GetValue<string>().Contains("Pending", StringComparison.Ordinal) &&
                    !drill["text"]!.GetValue<string>().Contains("Outside group", StringComparison.Ordinal);

                var inputRef = RefFor(drill, "\"Probe input\"");
                var write = await Run(new AgentRequest { Action = AgentActions.SetValue, Ref = inputRef, Text = "수정됨" });
                report["setValueWorks"] = write.Ok && write.Disposition == AgentDisposition.Dispatched && input.Text == "수정됨";
                var cleared = await Run(new AgentRequest { Action = AgentActions.SetValue, Ref = inputRef, Text = "" });
                report["clearValueWorks"] = cleared.Ok && input.Text.Length == 0;

                var buttonRef = RefFor(drill, "\"Probe invoke\"");
                var wait = new AgentRequest { Action = AgentActions.WaitFor, WindowTitle = form.Title, Role = "Text", Name = "Probe complete" };
                var before = await Run(wait);
                var click = await Run(new AgentRequest { Action = AgentActions.Invoke, Ref = buttonRef });
                var after = await Run(wait);
                report["invokeWorks"] = click.Ok && click.Disposition == AgentDisposition.Dispatched && invokes == 1;
                report["conditionWorks"] = before.Data?["matched"]?.GetValue<bool>() == false &&
                    after.Data?["matched"]?.GetValue<bool>() == true && after.Data["windowHandle"]?.GetValue<long>() == handle;

                var duplicateRef = RefFor(drill, "\"Probe duplicate\"");
                var ambiguous = await Run(new AgentRequest { Action = AgentActions.Invoke, Ref = duplicateRef });
                report["ambiguousRefRejected"] = ambiguous.ErrorCode == "ambiguous_target" &&
                    ambiguous.Disposition == AgentDisposition.NotDelivered && invokes == 1;
                var foreign = new UiAutomation(Path.GetFullPath(args[0])).Run(new AgentRequest { Action = AgentActions.Invoke, Ref = buttonRef });
                report["otherHelperRefRejected"] = foreign.ErrorCode == "stale_ref" && foreign.Disposition == AgentDisposition.NotDelivered;

                if (keepOpenMs > 0) await Task.Delay(keepOpenMs);
                form.Close();
                var stale = await Run(new AgentRequest { Action = AgentActions.Invoke, Ref = buttonRef });
                report["closedWindowRefRejected"] = stale.ErrorCode == "stale_ref" && stale.Disposition == AgentDisposition.NotDelivered;
                report["ok"] = report.Where(item => item.Key != "sessionId").All(item => item.Value?.GetValue<bool>() == true);
                report["observation"] = observed;
            }
            catch (Exception exception)
            {
                report["ok"] = false;
                report["error"] = exception.ToString();
            }
            File.WriteAllText(reportPath, report.ToJsonString(), new UTF8Encoding(false));
            app.Shutdown(report["ok"]?.GetValue<bool>() == true ? 0 : 1);
        };
        app.Run(form);

        Task<AgentResponse> Run(AgentRequest request) => Task.Run(() => ui.Run(request));
    }

    private static string RefFor(JsonObject observation, string name)
    {
        var lines = observation["text"]!.GetValue<string>().Split('\n');
        var row = Array.FindIndex(lines, line => line.Contains(name, StringComparison.Ordinal)) + 1;
        return observation["targets"]!.AsArray().OfType<JsonObject>()
            .First(target => target["index"]!.GetValue<int>() == row)["ref"]!.GetValue<string>();
    }
}
