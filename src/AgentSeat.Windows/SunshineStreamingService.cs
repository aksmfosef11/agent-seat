using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Models;

namespace AgentSeat.Windows;

public sealed record SunshineStreamingOptions(
    string TemplateDirectory,
    string StateDirectory,
    TimeSpan StartupTimeout,
    string SteamLauncherPath,
    string AppCompatDirectory);

public sealed class SunshineStreamingService : ISeatStreamingService, IDisposable
{
    private readonly SunshineStreamingOptions _options;
    private readonly ISessionProcessLauncher _launcher;
    private readonly SunshineRuntimeProvisioner _provisioner;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _seatGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _sunshineClient;

    public SunshineStreamingService(
        SunshineStreamingOptions options,
        ISessionProcessLauncher launcher)
    {
        _options = options with
        {
            TemplateDirectory = Path.GetFullPath(options.TemplateDirectory),
            StateDirectory = Path.GetFullPath(options.StateDirectory),
            SteamLauncherPath = Path.GetFullPath(options.SteamLauncherPath),
            AppCompatDirectory = Path.GetFullPath(options.AppCompatDirectory)
        };
        _launcher = launcher;
        _provisioner = new SunshineRuntimeProvisioner(
            _options.TemplateDirectory,
            _options.SteamLauncherPath,
            _options.AppCompatDirectory);
        Directory.CreateDirectory(_options.StateDirectory);

        _sunshineClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri is { IsLoopback: true }
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    public async Task<SunshineRuntimeStatus> GetStatusAsync(
        SeatDefinition seat,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ports = GetPorts(seat);
        if (!seat.StreamingEnabled)
        {
            return CreateStatus(
                seat,
                SunshineRuntimeState.Disabled,
                ports,
                detail: "Streaming is disabled for this seat.");
        }

        if (!_provisioner.TemplateExists)
        {
            return CreateStatus(
                seat,
                SunshineRuntimeState.MissingTemplate,
                ports,
                detail: $"Sunshine portable template is missing at '{_options.TemplateDirectory}'.");
        }

        if (!_provisioner.CompatibilityExists)
        {
            return CreateStatus(
                seat,
                SunshineRuntimeState.MissingCompatibility,
                ports,
                detail:
                    $"Steam compatibility isolation is incomplete. Expected the launcher at '{_options.SteamLauncherPath}' and x86/x64 runtimes at '{_options.AppCompatDirectory}'.");
        }

        SunshineProcessRecord? record;
        try
        {
            record = ReadProcessRecord(seat.Id);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return CreateStatus(
                seat,
                SunshineRuntimeState.Faulted,
                ports,
                detail: $"Could not read the Sunshine process record: {exception.Message}");
        }

        if (record is null)
        {
            return CreateStatus(
                seat,
                SunshineRuntimeState.Stopped,
                ports,
                gamepadReady: IsViGEmBusInstalled(),
                detail: "Sunshine is stopped. Connect the seat with RDP, then start streaming in that session.");
        }

        using var process = TryOpenVerifiedProcess(record, out var verificationFailure);
        var log = ReadLogSummary(record.LogFile);
        if (process is null)
        {
            var detail = log.Diagnostic ?? $"Sunshine stopped unexpectedly: {verificationFailure}";
            return CreateStatus(
                seat,
                SunshineRuntimeState.Faulted,
                ports,
                sessionId: record.SessionId,
                encoderReady: log.EncoderReady,
                gamepadReady: IsViGEmBusInstalled(),
                startedAtUtc: record.StartedAtUtc,
                detail: detail);
        }

        var ready = await IsTcpListenerReadyAsync(ports!.WebUiHttps, cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            return CreateStatus(
                seat,
                SunshineRuntimeState.Starting,
                ports,
                record.SessionId,
                record.ProcessId,
                log.EncoderReady,
                IsViGEmBusInstalled(),
                record.StartedAtUtc,
                log.Diagnostic ?? "Sunshine is running and still initializing capture and encoders.");
        }

        var gamepadReady = IsViGEmBusInstalled();
        return CreateStatus(
            seat,
            SunshineRuntimeState.Ready,
            ports,
            record.SessionId,
            record.ProcessId,
            log.EncoderReady,
            gamepadReady,
            record.StartedAtUtc,
            gamepadReady
                ? "Moonlight video, keyboard, mouse, and virtual gamepad paths are ready."
                : "Moonlight video, keyboard, and mouse are ready. Install ViGEmBus for virtual gamepad input.");
    }

    public async Task<SunshineRuntimeStatus> StartAsync(
        SeatDefinition seat,
        SeatSession session,
        CancellationToken cancellationToken = default)
    {
        if (!seat.StreamingEnabled)
        {
            throw new InvalidOperationException($"Streaming is disabled for seat '{seat.Id}'.");
        }

        if (session.State is not (SeatSessionState.Active or SeatSessionState.Connected or SeatSessionState.Shadow))
        {
            throw new InvalidOperationException(
                $"Windows session {session.SessionId} is {session.State}. Keep the RDP connection active while starting Sunshine.");
        }

        var gate = GateFor(seat.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await GetStatusAsync(seat, cancellationToken).ConfigureAwait(false);
            if (current.ProcessId is not null)
            {
                if (current.SessionId != session.SessionId)
                {
                    throw new InvalidOperationException(
                        $"Sunshine is already running in session {current.SessionId}; stop it before moving the seat.");
                }

                return current;
            }

            if (!_provisioner.TemplateExists)
            {
                throw new DirectoryNotFoundException(
                    $"Sunshine is not installed. Run scripts/Get-Sunshine.ps1; expected '{_options.TemplateDirectory}'.");
            }

            if (!_provisioner.CompatibilityExists)
            {
                throw new DirectoryNotFoundException(
                    $"Steam compatibility isolation is incomplete at '{_options.AppCompatDirectory}'. Run scripts/Build-AppCompat.ps1 and publish AgentSeat again.");
            }

            var ports = SunshinePorts.FromBasePort(seat.SunshineBasePort);
            EnsurePortsAvailable(ports);
            var userProfile = _launcher.GetUserProfilePath(session.SessionId);
            var instance = _provisioner.Provision(seat, userProfile);
            var launched = _launcher.Start(
                session.SessionId,
                instance.ExecutablePath,
                [instance.ConfigurationFile],
                instance.RuntimeDirectory);
            var record = new SunshineProcessRecord(
                1,
                seat.Id,
                launched.ProcessId,
                session.SessionId,
                launched.StartedAtUtc,
                instance.RuntimeDirectory,
                instance.ExecutablePath,
                instance.LogFile);
            WriteProcessRecord(record);

            var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
            SunshineRuntimeStatus status;
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
                status = await GetStatusAsync(seat, cancellationToken).ConfigureAwait(false);
                if (status.State is SunshineRuntimeState.Ready or SunshineRuntimeState.Faulted)
                {
                    return status;
                }
            }
            while (DateTimeOffset.UtcNow < deadline);

            return status!;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SunshineRuntimeStatus> StopAsync(
        SeatDefinition seat,
        CancellationToken cancellationToken = default)
    {
        var gate = GateFor(seat.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = ReadProcessRecord(seat.Id);
            if (record is null)
            {
                return await GetStatusAsync(seat, cancellationToken).ConfigureAwait(false);
            }

            using var process = TryOpenVerifiedProcess(record, out _);
            if (process is not null)
            {
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            DeleteProcessRecord(seat.Id);
            return CreateStatus(
                seat,
                SunshineRuntimeState.Stopped,
                GetPorts(seat),
                gamepadReady: IsViGEmBusInstalled(),
                detail: "Sunshine was stopped. Pairing state and applications were preserved.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SunshinePairResult> PairAsync(
        SeatDefinition seat,
        string pin,
        string clientName,
        CancellationToken cancellationToken = default)
    {
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Moonlight PIN must contain exactly four digits.", nameof(pin));
        }

        clientName = clientName.Trim();
        if (clientName.Length is < 1 or > 80 || clientName.Contains('\r') || clientName.Contains('\n'))
        {
            throw new ArgumentException("Moonlight client name must contain 1 to 80 characters.", nameof(clientName));
        }

        var status = await GetStatusAsync(seat, cancellationToken).ConfigureAwait(false);
        if (status.State != SunshineRuntimeState.Ready)
        {
            throw new InvalidOperationException("Start the seat's Sunshine stream before pairing Moonlight.");
        }

        var record = ReadProcessRecord(seat.Id) ??
                     throw new InvalidOperationException("Sunshine process state disappeared before pairing.");
        var secret = _provisioner.ReadSecret(record.RuntimeDirectory, seat.Id);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://127.0.0.1:{status.Ports!.WebUiHttps}/api/pin");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{secret.Username}:{secret.Password}")));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { pin, name = clientName }),
            Encoding.UTF8,
            "application/json");

        using var response = await _sunshineClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Sunshine rejected the pairing request ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(body);
        var accepted = document.RootElement.TryGetProperty("status", out var value) && value.GetBoolean();
        return new SunshinePairResult(
            accepted,
            accepted
                ? $"Moonlight client '{clientName}' was paired."
                : "Sunshine did not have a matching pending Moonlight PIN. Start pairing in Moonlight and submit the new PIN immediately.");
    }

    public void Dispose()
    {
        _sunshineClient.Dispose();
        foreach (var gate in _seatGates.Values)
        {
            gate.Dispose();
        }
    }

    private SemaphoreSlim GateFor(string seatId) =>
        _seatGates.GetOrAdd(seatId, static _ => new SemaphoreSlim(1, 1));

    private static SunshinePorts? GetPorts(SeatDefinition seat) =>
        seat.StreamingEnabled && seat.SunshineBasePort >= SunshinePorts.MinimumBasePort
            ? SunshinePorts.FromBasePort(seat.SunshineBasePort)
            : null;

    private static SunshineRuntimeStatus CreateStatus(
        SeatDefinition seat,
        SunshineRuntimeState state,
        SunshinePorts? ports,
        int? sessionId = null,
        int? processId = null,
        bool encoderReady = false,
        bool gamepadReady = false,
        DateTimeOffset? startedAtUtc = null,
        string detail = "") => new(
        state,
        ports,
        ports is null ? seat.HostAddress : FormatMoonlightAddress(seat.HostAddress, ports.Base),
        sessionId,
        processId,
        encoderReady,
        gamepadReady,
        startedAtUtc,
        detail);

    private static string FormatMoonlightAddress(string host, int port)
    {
        host = host.Trim();
        if (host.Contains(':') && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }

        return $"{host}:{port}";
    }

    private static void EnsurePortsAvailable(SunshinePorts ports)
    {
        try
        {
            var tcpListeners = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
            var tcpConflict = ports.TcpPorts.Concat(ports.LocalOnlyPorts).FirstOrDefault(tcpListeners.Contains);
            if (tcpConflict != 0)
            {
                throw new InvalidOperationException($"TCP port {tcpConflict} is already in use.");
            }

            var udpListeners = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
            var udpConflict = ports.UdpPorts.FirstOrDefault(udpListeners.Contains);
            if (udpConflict != 0)
            {
                throw new InvalidOperationException($"UDP port {udpConflict} is already in use.");
            }
        }
        catch (NetworkInformationException exception)
        {
            throw new InvalidOperationException("Windows could not inspect Sunshine port availability.", exception);
        }
    }

    private static async Task<bool> IsTcpListenerReadyAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(450));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private Process? TryOpenVerifiedProcess(SunshineProcessRecord record, out string failure)
    {
        failure = string.Empty;
        try
        {
            var process = Process.GetProcessById(record.ProcessId);
            if (process.HasExited)
            {
                process.Dispose();
                failure = "the recorded process has exited";
                return null;
            }

            if (process.SessionId != record.SessionId)
            {
                process.Dispose();
                failure = "the process ID now belongs to a different Windows session";
                return null;
            }

            var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            if ((actualStart - record.StartedAtUtc).Duration() > TimeSpan.FromSeconds(3))
            {
                process.Dispose();
                failure = "the process ID was reused by a newer process";
                return null;
            }

            var actualExecutable = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
            if (!string.Equals(
                    actualExecutable,
                    Path.GetFullPath(record.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                failure = "the process executable does not match AgentSeat's Sunshine instance";
                return null;
            }

            return process;
        }
        catch (ArgumentException)
        {
            failure = "the recorded process no longer exists";
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            failure = $"the recorded process could not be verified: {exception.Message}";
            return null;
        }
    }

    private string ProcessRecordPath(string seatId) => Path.Combine(_options.StateDirectory, $"{seatId}.json");

    private SunshineProcessRecord? ReadProcessRecord(string seatId)
    {
        var path = ProcessRecordPath(seatId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<SunshineProcessRecord>(File.ReadAllText(path, Encoding.UTF8)) ??
              throw new JsonException($"Process record '{path}' was empty.")
            : null;
    }

    private void WriteProcessRecord(SunshineProcessRecord record)
    {
        Directory.CreateDirectory(_options.StateDirectory);
        var path = ProcessRecordPath(record.SeatId);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    private void DeleteProcessRecord(string seatId)
    {
        var path = ProcessRecordPath(seatId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static LogSummary ReadLogSummary(string logFile)
    {
        if (!File.Exists(logFile))
        {
            return new LogSummary(false, null);
        }

        try
        {
            using var stream = new FileStream(
                logFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var tail = new Queue<string>(200);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == 200)
                {
                    _ = tail.Dequeue();
                }

                tail.Enqueue(line);
            }

            var lines = tail.ToArray();
            var encoderReady = lines.Any(line =>
                line.Contains("Found H.264 encoder:", StringComparison.OrdinalIgnoreCase));
            var diagnostic = lines.LastOrDefault(line =>
                (line.Contains("Fatal:", StringComparison.OrdinalIgnoreCase) &&
                 !line.Contains("ViGEmBus", StringComparison.OrdinalIgnoreCase)) ||
                line.Contains("Couldn't find any working encoder", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Unable to find display", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Failed to initialize", StringComparison.OrdinalIgnoreCase));
            return new LogSummary(encoderReady, diagnostic);
        }
        catch (IOException)
        {
            return new LogSummary(false, null);
        }
    }

    private static bool IsViGEmBusInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus");
        return key is not null;
    }

    private sealed record SunshineProcessRecord(
        int Version,
        string SeatId,
        int ProcessId,
        int SessionId,
        DateTimeOffset StartedAtUtc,
        string RuntimeDirectory,
        string ExecutablePath,
        string LogFile);

    private sealed record LogSummary(bool EncoderReady, string? Diagnostic);
}
