using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using AgentSeat.AgentHelper;
using AgentSeat.Core.Agent;

// AgentSeat.AgentHelper --seat <id> --allow-sid <sid>
//
// Runs inside a seat's WTS session as the seat user (started by the AgentSeat service). It owns
// that session's screen and input, and takes commands over a named pipe that only the SID passed
// in --allow-sid (the service's identity) may open.
NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.PerMonitorAwareV2);
// UI providers can hang. A disposable MTA worker performs read-only observation; it opens no input pipe
// and, unlike a regular helper, never releases or injects any keys. Its parent enforces a timeout.
if (args is ["--observe-uia"])
{
    return UiObservation.RunWorker();
}
AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
    HelperLog.Write($"Helper crashed: {eventArgs.ExceptionObject}");

string? seatId = null;
string? allowedSid = null;
var releaseOnly = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--seat" when index + 1 < args.Length:
            seatId = args[++index];
            break;
        case "--allow-sid" when index + 1 < args.Length:
            allowedSid = args[++index];
            break;
        case "--release-inputs":
            releaseOnly = true;
            break;
    }
}

if (seatId is null || !Regex.IsMatch(seatId, "^[a-z][a-z0-9-]{0,31}$") || (allowedSid is null && !releaseOnly))
{
    return 2;
}

HelperLog.Initialize(seatId);
if (releaseOnly)
{
    // AgentSeat.AgentHelper --seat <id> --release-inputs: started by the kill switch right after it killed the
    // helper, so keys or buttons the dead helper was holding do not stay down. Opens no pipe and exits.
    InputInjector.ReleaseEverything();
    HelperLog.Write($"Released held keys and buttons for seat '{seatId}' after the helper was stopped.");
    return 0;
}

if (allowedSid is null)
{
    return 2;
}

SecurityIdentifier allowed;
try
{
    allowed = new SecurityIdentifier(allowedSid);
}
catch (ArgumentException)
{
    HelperLog.Write($"Rejected invalid --allow-sid '{allowedSid}'.");
    return 2;
}

var security = new PipeSecurity();
// Network logons are denied outright; the pipe is reachable over SMB otherwise.
security.AddAccessRule(new PipeAccessRule(
    new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
    PipeAccessRights.FullControl,
    AccessControlType.Deny));
security.AddAccessRule(new PipeAccessRule(
    allowed,
    PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
    AccessControlType.Allow));

NamedPipeServerStream server;
try
{
    server = NamedPipeServerStreamAcl.Create(
        AgentPipe.NameFor(seatId),
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
        inBufferSize: 1 << 16,
        outBufferSize: 1 << 16,
        security);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    // Another helper (or a squatter) already owns the name; never share it. Windows reports an existing
    // FIRST_PIPE_INSTANCE name as either an I/O error or access denied.
    HelperLog.Write($"Pipe already exists, exiting: {exception.Message}");
    return 3;
}

// A previous helper killed mid-click or mid-chord may have left modifiers or buttons held in this session.
InputInjector.ReleaseEverything();
HelperLog.Write($"Helper started for seat '{seatId}' (pid {Environment.ProcessId}), allowing {allowed.Value}.");
using (server)
{
    var dispatcher = new AgentDispatcher();
    using var shutdown = new CancellationTokenSource();
    // Dead-man switch for key_down / mouse_down: an agent that crashed or forgot must not leave input held for good.
    var idleLimit = TimeSpan.FromSeconds(60);
    using var idleRelease = new System.Threading.Timer(
        _ =>
        {
            if (InputInjector.ReleaseIfIdle(idleLimit) is { Count: > 0 } released)
            {
                HelperLog.Write($"Released held input after {idleLimit.TotalSeconds:0} s without an action: {string.Join(", ", released)}.");
            }
        },
        state: null,
        dueTime: TimeSpan.FromSeconds(5),
        period: TimeSpan.FromSeconds(5));
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    while (!shutdown.IsCancellationRequested)
    {
        try
        {
            await server.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
            await ServeConnectionAsync(server, dispatcher, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            break; // Normal shutdown.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
                                              or InvalidOperationException or ObjectDisposedException)
        {
            HelperLog.Write($"Connection dropped: {exception.Message}");
        }
        finally
        {
            // A client that hangs up leaves the pipe "broken" (IsConnected is already false), and a broken
            // instance must be disconnected explicitly before it can accept the next client.
            try
            {
                server.Disconnect();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                // Already disconnected.
            }
        }
    }
}

HelperLog.Write("Helper stopped.");
return 0;

static async Task ServeConnectionAsync(Stream pipe, AgentDispatcher dispatcher, CancellationToken cancellationToken)
{
    while (true)
    {
        var payload = await AgentFraming.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return;
        }

        AgentResponse response;
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<AgentRequest>(payload, AgentJson.Options);
            response = request is null
                ? AgentResponse.Failure(AgentControlException.InvalidRequest, "Empty request.")
                : dispatcher.Handle(request);
        }
        catch (System.Text.Json.JsonException exception)
        {
            response = AgentResponse.Failure(AgentControlException.InvalidRequest, $"Malformed request: {exception.Message}");
        }

        await AgentFraming.WriteJsonAsync(pipe, response, cancellationToken).ConfigureAwait(false);
    }
}
