using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 2 or > 3) return;
        var helper = args[0];
        var reportPath = args[1];
        var keepOpenMs = args.Length == 3 && int.TryParse(args[2], out var delay) ? Math.Clamp(delay, 0, 60000) : 0;
        ApplicationConfiguration.Initialize();
        using var form = new Form { Text = "AgentSeat UIA verification", Width = 520, Height = 260 };
        var input = new TextBox { Text = "hello 안녕하세요", AccessibleName = "Probe input", Left = 20, Top = 20, Width = 300 };
        var password = new TextBox { Text = "uia-probe-secret-should-never-leak", UseSystemPasswordChar = true,
            AccessibleName = "Probe password", Left = 20, Top = 60, Width = 300 };
        var checkbox = new CheckBox { Text = "Probe checked", Checked = true, Left = 20, Top = 100, Width = 300 };
        form.Controls.AddRange([input, password, checkbox]);
        form.Shown += async (_, _) =>
        {
            var report = new JsonObject();
            try
            {
                input.Focus();
                await Task.Delay(250);
                var observed = await ReadAsync(helper, form.Handle.ToInt64(), 60, 4000);
                var text = observed["text"]?.GetValue<string>() ?? "";
                report["sessionId"] = Process.GetCurrentProcess().SessionId;
                report["available"] = observed["available"]?.DeepClone();
                report["inputValueRead"] = text.Contains("hello 안녕하세요", StringComparison.Ordinal);
                report["focusRead"] = text.Contains("focused", StringComparison.Ordinal);
                report["toggleRead"] = text.Contains("toggle=On", StringComparison.Ordinal);
                report["passwordHidden"] = !observed.ToJsonString().Contains(password.Text, StringComparison.Ordinal) &&
                    text.Contains("[password]", StringComparison.Ordinal);
                var bounded = await ReadAsync(helper, form.Handle.ToInt64(), 1, 256);
                report["elementLimitWorks"] = bounded["elements"]?.GetValue<int>() == 1 && bounded["truncated"]?.GetValue<bool>() == true;
                report["characterLimitWorks"] = (bounded["text"]?.GetValue<string>().Length ?? 0) <= 256;
                report["ok"] = report.Where(item => item.Key is not ("sessionId" or "available"))
                    .All(item => item.Value?.GetValue<bool>() == true) && observed["available"]?.GetValue<bool>() == true;
                report["observation"] = observed;
            }
            catch (Exception exception)
            {
                report["ok"] = false;
                report["error"] = exception.ToString();
            }
            File.WriteAllText(reportPath, report.ToJsonString(), new UTF8Encoding(false));
            if (keepOpenMs > 0) await Task.Delay(keepOpenMs);
            form.Close();
        };
        Application.Run(form);
    }

    private static async Task<JsonObject> ReadAsync(string helper, long handle, int elements, int characters)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        } };
        process.StartInfo.ArgumentList.Add("--observe-uia");
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(new JsonObject
        {
            ["action"] = "observe", ["handle"] = handle, ["maxElements"] = elements, ["maxCharacters"] = characters
        }.ToJsonString());
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var text = await output;
        var error = await errors;
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException($"Worker exited {process.ExitCode}: {error}");
        return JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException("No UI observation: " + error);
    }
}
