using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AgentSeat.RdpAnchor;

internal static partial class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [STAThread]
    internal static int Main(string[] args)
    {
        try
        {
            var command = AnchorCommand.Parse(args);
            if (command.Options.TryGetValue("--root", out var explicitRoot)) AnchorPaths.OverrideRoot = Path.GetFullPath(explicitRoot);
            return command.Name switch
            {
                "configure" => Configure(command),
                "run" => Run(command),
                "status" => Status(command),
                "diagnose" => Diagnose(command),
                _ => throw new ArgumentException("Unknown RDP anchor command.")
            };
        }
        catch (Exception exception) when (exception is
                   ArgumentException or IOException or UnauthorizedAccessException or
                   InvalidDataException or CryptographicException or Win32Exception or
                   InvalidOperationException or JsonException)
        {
            MessageOrConsoleError($"AgentSeat RDP anchor failed: {exception.Message}");
            return 1;
        }
    }

    private static int Configure(AnchorCommand command)
    {
        var options = AnchorOptions.FromCommand(command);
        var password = Console.In.ReadToEnd().TrimEnd('\r', '\n');
        if (password.Length is < 12 or > 256 || password.Contains('\0'))
        {
            throw new ArgumentException("A 12 to 256 character password must be supplied on standard input.");
        }

        var directory = AnchorPaths.Directory(options.SeatId);
        Directory.CreateDirectory(directory);
        AtomicWrite(
            AnchorPaths.Configuration(options.SeatId),
            JsonSerializer.SerializeToUtf8Bytes(options, JsonOptions));

        var plaintext = Encoding.Unicode.GetBytes(password);
        try
        {
            AtomicWrite(
                AnchorPaths.Secret(options.SeatId),
                Dpapi.Protect(plaintext, Entropy(options.SeatId)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        Console.WriteLine($"Configured hidden RDP anchor '{options.SeatId}' for {options.Domain}\\{options.UserName}.");
        return 0;
    }

    private static int Run(AnchorCommand command)
    {
        var seatId = command.Required("--seat");
        ValidateSeatId(seatId);
        using var mutex = new Mutex(initiallyOwned: true, $"Local\\AgentSeat_RdpAnchor_{seatId}", out var acquired);
        if (!acquired)
        {
            return 0;
        }

        var options = ReadOptions(seatId);
        var protectedSecret = File.ReadAllBytes(AnchorPaths.Secret(seatId));
        var plaintext = Dpapi.Unprotect(protectedSecret, Entropy(seatId));
        string password;
        try
        {
            password = Encoding.Unicode.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new AnchorForm(options, password);
        password = string.Empty;
        Application.Run(form);
        return 0;
    }

    private static int Status(AnchorCommand command)
    {
        var seatId = command.Required("--seat");
        ValidateSeatId(seatId);
        var statusFile = AnchorPaths.Status(seatId);
        Console.WriteLine(File.Exists(statusFile) ? File.ReadAllText(statusFile, Encoding.UTF8) : "{}");
        return 0;
    }

    private static int Diagnose(AnchorCommand command)
    {
        var seatId = command.Required("--seat");
        ValidateSeatId(seatId);
        string? error = null;
        try
        {
            _ = ReadOptions(seatId);
            var plaintext = Dpapi.Unprotect(File.ReadAllBytes(AnchorPaths.Secret(seatId)), Entropy(seatId));
            CryptographicOperations.ZeroMemory(plaintext);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException or ArgumentException or JsonException)
        { error = exception.Message; }
        Console.WriteLine(JsonSerializer.Serialize(new { seatId, configurationPath = AnchorPaths.Configuration(seatId),
            credentialExists = File.Exists(AnchorPaths.Secret(seatId)), credentialsUsable = error is null,
            sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId, error }, JsonOptions));
        return error is null ? 0 : 1;
    }

    private static AnchorOptions ReadOptions(string seatId)
    {
        var path = AnchorPaths.Configuration(seatId);
        var options = JsonSerializer.Deserialize<AnchorOptions>(File.ReadAllBytes(path), JsonOptions) ??
                      throw new InvalidDataException($"RDP anchor configuration '{path}' was empty.");
        options.Validate();
        if (!string.Equals(options.SeatId, seatId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("RDP anchor configuration belongs to a different seat.");
        }

        return options;
    }

    private static void AtomicWrite(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static void WriteStatus(AnchorStatus status)
    {
        AtomicWrite(
            AnchorPaths.Status(status.SeatId),
            JsonSerializer.SerializeToUtf8Bytes(status, JsonOptions));
    }

    private static byte[] Entropy(string seatId) =>
        Encoding.UTF8.GetBytes($"AgentSeat/RdpAnchor/{seatId}/v1");

    private static void MessageOrConsoleError(string message)
    {
        if (Environment.UserInteractive && !Console.IsErrorRedirected)
        {
            MessageBox.Show(message, "AgentSeat RDP Anchor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            Console.Error.WriteLine(message);
        }
    }

    internal static void ValidateSeatId(string seatId)
    {
        if (!SeatIdRegex().IsMatch(seatId))
        {
            throw new ArgumentException("Seat ID must contain only lowercase letters, numbers, and hyphens.");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SeatIdRegex();

    private static class Dpapi
    {
        private const int UiForbidden = 0x1;

        internal static byte[] Protect(byte[] input, byte[] entropy) =>
            Transform(input, entropy, protect: true);

        internal static byte[] Unprotect(byte[] input, byte[] entropy) =>
            Transform(input, entropy, protect: false);

        private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
        {
            var inputPointer = Marshal.AllocHGlobal(input.Length);
            var entropyPointer = Marshal.AllocHGlobal(entropy.Length);
            try
            {
                Marshal.Copy(input, 0, inputPointer, input.Length);
                Marshal.Copy(entropy, 0, entropyPointer, entropy.Length);
                var inputBlob = new DataBlob(input.Length, inputPointer);
                var entropyBlob = new DataBlob(entropy.Length, entropyPointer);
                IntPtr description = IntPtr.Zero;
                DataBlob output;
                var succeeded = protect
                    ? NativeMethods.CryptProtectData(
                        ref inputBlob,
                        "AgentSeat RDP anchor credential",
                        ref entropyBlob,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        UiForbidden,
                        out output)
                    : NativeMethods.CryptUnprotectData(
                        ref inputBlob,
                        out description,
                        ref entropyBlob,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        UiForbidden,
                        out output);
                if (description != IntPtr.Zero)
                {
                    _ = NativeMethods.LocalFree(description);
                }
                if (!succeeded)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not transform the RDP credential.");
                }

                try
                {
                    var bytes = new byte[output.Length];
                    Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally
                {
                    _ = NativeMethods.LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inputPointer);
                Marshal.FreeHGlobal(entropyPointer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct DataBlob(int length, IntPtr data)
        {
            internal readonly int Length = length;
            internal readonly IntPtr Data = data;
        }

        private static class NativeMethods
        {
            [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptProtectData(
                ref DataBlob dataIn,
                string description,
                ref DataBlob optionalEntropy,
                IntPtr reserved,
                IntPtr promptStruct,
                int flags,
                out DataBlob dataOut);

            [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptUnprotectData(
                ref DataBlob dataIn,
                out IntPtr description,
                ref DataBlob optionalEntropy,
                IntPtr reserved,
                IntPtr promptStruct,
                int flags,
                out DataBlob dataOut);

            [DllImport("kernel32.dll")]
            internal static extern IntPtr LocalFree(IntPtr memory);
        }
    }
}

internal sealed record AnchorCommand(string Name, IReadOnlyDictionary<string, string> Options)
{
    internal static AnchorCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "--help" or "-h")
        {
            throw new ArgumentException(
                "Usage: AgentSeat.RdpAnchor configure|run|status --seat <id> [options]");
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{args[index]}' requires a value.");
            }
            if (!options.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException($"Option '{args[index]}' can be specified only once.");
            }
        }

        return new AnchorCommand(args[0].ToLowerInvariant(), options);
    }

    internal string Required(string name) =>
        Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required.");

    internal int Integer(string name, int fallback) =>
        Options.TryGetValue(name, out var value)
            ? int.TryParse(value, out var result)
                ? result
                : throw new ArgumentException($"{name} requires an integer.")
            : fallback;
}

internal sealed record AnchorOptions(
    string SeatId,
    string Server,
    int Port,
    string Domain,
    string UserName,
    int Width,
    int Height)
{
    internal static AnchorOptions FromCommand(AnchorCommand command)
    {
        var options = new AnchorOptions(
            command.Required("--seat"),
            command.Required("--server"),
            command.Integer("--port", 3389),
            command.Required("--domain"),
            command.Required("--user"),
            command.Integer("--width", 1920),
            command.Integer("--height", 1080));
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        Program.ValidateSeatId(SeatId);
        if (string.IsNullOrWhiteSpace(Server) || Server.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("RDP server is invalid.");
        }
        if (Port is < 1 or > 65535 || Width is < 640 or > 7680 || Height is < 480 or > 4320)
        {
            throw new ArgumentException("RDP port or display dimensions are invalid.");
        }
        if (string.IsNullOrWhiteSpace(Domain) || string.IsNullOrWhiteSpace(UserName) ||
            Domain.IndexOfAny(['\\', '\r', '\n', '\0']) >= 0 ||
            UserName.IndexOfAny(['\\', '@', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("RDP account is invalid.");
        }
    }
}

internal sealed record AnchorStatus(
    string SeatId,
    string State,
    DateTimeOffset UpdatedAtUtc,
    int ProcessId,
    string Detail);

internal static class AnchorPaths
{
    internal static string? OverrideRoot { get; set; }
    private static string Root
    {
        get
        {
            var configured = OverrideRoot ?? Environment.GetEnvironmentVariable("AGENTSEAT_ANCHOR_ROOT");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "agent-seat",
                    "RdpAnchors",
                    System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Anchor owner SID missing."))
                : Path.GetFullPath(configured);
        }
    }

    internal static string Directory(string seatId) => Path.Combine(Root, seatId);
    internal static string Configuration(string seatId) => Path.Combine(Directory(seatId), "anchor.json");
    internal static string Secret(string seatId) => Path.Combine(Directory(seatId), "credential.dat");
    internal static string Status(string seatId) => Path.Combine(Directory(seatId), "status.json");
}

internal sealed class AnchorForm : Form
{
    private const string RdpClientClassId = "1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8";
    private readonly AnchorOptions _options;
    private readonly string _password;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private RdpControl? _control;
    private DateTimeOffset _lastConnectAttempt;
    private bool _wasConnected;

    internal AnchorForm(AnchorOptions options, string password)
    {
        _options = options;
        _password = password;
        Text = $"AgentSeat anchor - {options.SeatId}";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        ClientSize = new Size(Math.Max(16, options.Width), Math.Max(16, options.Height));
        Opacity = 0.01;
        _timer.Tick += (_, _) => Poll();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ConnectFresh();
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _control?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ConnectFresh()
    {
        _control?.Dispose();
        _control = new RdpControl(RdpClientClassId)
        {
            Dock = DockStyle.Fill
        };
        ((ISupportInitialize)_control).BeginInit();
        Controls.Clear();
        Controls.Add(_control);
        ((ISupportInitialize)_control).EndInit();
        _control.CreateControl();

        var client = _control.Client;
        Com.Set(client, "Server", _options.Server);
        Com.Set(client, "Domain", _options.Domain);
        Com.Set(client, "UserName", _options.UserName);
        Com.Set(client, "DesktopWidth", _options.Width);
        Com.Set(client, "DesktopHeight", _options.Height);
        Com.Set(client, "ColorDepth", 32);

        var advanced = Com.GetFirst(client, "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings2");
        Com.Set(advanced, "ClearTextPassword", _password);
        Com.Set(advanced, "RDPPort", _options.Port);
        Com.TrySet(advanced, "EnableCredSspSupport", true);
        Com.TrySet(advanced, "AuthenticationLevel", 0u);
        Com.TrySet(advanced, "PromptForCredentials", false);
        Com.TrySet(advanced, "SmartSizing", false);
        Com.TrySet(advanced, "DisplayConnectionBar", false);
        Com.TrySet(advanced, "RedirectClipboard", false);
        Com.TrySet(advanced, "RedirectDrives", false);
        Com.TrySet(advanced, "RedirectPrinters", false);
        Com.TrySet(advanced, "RedirectSmartCards", false);
        Com.TrySet(advanced, "AudioRedirectionMode", 0u);
        Com.TrySet(advanced, "EnableAutoReconnect", true);
        Com.TrySet(advanced, "MaxReconnectAttempts", 20u);

        _lastConnectAttempt = DateTimeOffset.UtcNow;
        Com.Call(client, "Connect");
        Program.WriteStatus(new AnchorStatus(
            _options.SeatId,
            "connecting",
            DateTimeOffset.UtcNow,
            Environment.ProcessId,
            $"Connecting to {_options.Server}:{_options.Port} as {_options.Domain}\\{_options.UserName}."));
    }

    private void Poll()
    {
        try
        {
            var connected = _control is not null && Convert.ToInt32(
                Com.Get(_control.Client, "Connected"),
                System.Globalization.CultureInfo.InvariantCulture) != 0;
            if (connected != _wasConnected || DateTimeOffset.UtcNow.Second % 10 == 0)
            {
                Program.WriteStatus(new AnchorStatus(
                    _options.SeatId,
                    connected ? "connected" : "connecting",
                    DateTimeOffset.UtcNow,
                    Environment.ProcessId,
                    connected
                        ? $"Hidden display anchor is connected at {_options.Width}x{_options.Height}."
                        : "Waiting for the local RDP session."));
            }
            _wasConnected = connected;

            if (!connected && DateTimeOffset.UtcNow - _lastConnectAttempt > TimeSpan.FromSeconds(30))
            {
                ConnectFresh();
            }
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException or InvalidOperationException)
        {
            Program.WriteStatus(new AnchorStatus(
                _options.SeatId,
                "faulted",
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                exception.InnerException?.Message ?? exception.Message));
            if (DateTimeOffset.UtcNow - _lastConnectAttempt > TimeSpan.FromSeconds(30))
            {
                _lastConnectAttempt = DateTimeOffset.UtcNow;
                try
                {
                    ConnectFresh();
                }
                catch (Exception retryException)
                {
                    Program.WriteStatus(new AnchorStatus(
                        _options.SeatId,
                        "faulted",
                        DateTimeOffset.UtcNow,
                        Environment.ProcessId,
                        retryException.InnerException?.Message ?? retryException.Message));
                }
            }
        }
    }
}

internal sealed class RdpControl(string classId) : AxHost(classId)
{
    internal object Client => GetOcx();
}

internal static class Com
{
    private const BindingFlags GetProperty = BindingFlags.GetProperty;
    private const BindingFlags SetProperty = BindingFlags.SetProperty;
    private const BindingFlags InvokeMethod = BindingFlags.InvokeMethod;

    internal static object Get(object target, string name) =>
        target.GetType().InvokeMember(name, GetProperty, null, target, null) ??
        throw new InvalidOperationException($"RDP property '{name}' returned no value.");

    internal static object GetFirst(object target, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                return Get(target, name);
            }
            catch (Exception exception) when (exception is MissingMethodException or COMException or TargetInvocationException)
            {
                // Older Windows builds expose an earlier AdvancedSettings interface.
            }
        }
        throw new InvalidOperationException("The Windows RDP control exposes no compatible advanced settings interface.");
    }

    internal static void Set(object target, string name, object value) =>
        _ = target.GetType().InvokeMember(name, SetProperty, null, target, [value]);

    internal static void TrySet(object target, string name, object value)
    {
        try
        {
            Set(target, name, value);
        }
        catch (Exception exception) when (exception is MissingMethodException or COMException or TargetInvocationException)
        {
            // Optional setting on older RDP ActiveX versions.
        }
    }

    internal static void Call(object target, string name) =>
        _ = target.GetType().InvokeMember(name, InvokeMethod, null, target, null);
}
