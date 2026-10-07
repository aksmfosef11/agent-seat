using System.Reflection;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Agent;
using AgentSeat.Core.Models;
using AgentSeat.Core.Services;
using AgentSeat.Core.Storage;
using AgentSeat.Service;
using AgentSeat.Windows;

var packagedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var contentRoot = Directory.Exists(packagedWebRoot)
    ? AppContext.BaseDirectory
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var sourceWebRoot = Path.Combine(contentRoot, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = Directory.Exists(packagedWebRoot) ? packagedWebRoot : sourceWebRoot
});
builder.Host.UseWindowsService(options => options.ServiceName = "agent-seat");

var listenUrl = builder.Configuration["AgentSeat:ListenUrl"] ??
                Environment.GetEnvironmentVariable("AGENTSEAT_URLS") ??
                "http://127.0.0.1:38399";
EnsureLoopbackOnly(listenUrl);
builder.WebHost.UseUrls(listenUrl);

var dataFile = builder.Configuration["AgentSeat:DataFile"] ??
               Environment.GetEnvironmentVariable("AGENTSEAT_DATA") ??
               Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                   "agent-seat",
                   "agent-seat.json");
dataFile = Path.GetFullPath(dataFile);
var dataDirectory = Path.GetDirectoryName(dataFile) ??
                    throw new InvalidOperationException("AgentSeat data file must have a parent directory.");
var sunshineTemplate = builder.Configuration["AgentSeat:SunshineTemplate"] ??
                       Environment.GetEnvironmentVariable("AGENTSEAT_SUNSHINE_TEMPLATE") ??
                       Path.Combine(AppContext.BaseDirectory, "sunshine");
var sunshineStateDirectory = builder.Configuration["AgentSeat:SunshineStateDirectory"] ??
                             Environment.GetEnvironmentVariable("AGENTSEAT_SUNSHINE_STATE") ??
                              Path.Combine(dataDirectory, "runtime", "sunshine");
var steamLauncher = builder.Configuration["AgentSeat:SteamLauncher"] ??
                    Environment.GetEnvironmentVariable("AGENTSEAT_STEAM_LAUNCHER") ??
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "app-launcher",
                        "AgentSeat.SteamLauncher.exe");
var appCompatDirectory = builder.Configuration["AgentSeat:AppCompatDirectory"] ??
                         Environment.GetEnvironmentVariable("AGENTSEAT_APPCOMPAT") ??
                         Path.Combine(AppContext.BaseDirectory, "compat");
var agentHelper = builder.Configuration["AgentSeat:AgentHelper"] ??
                  Environment.GetEnvironmentVariable("AGENTSEAT_AGENT_HELPER") ??
                  Path.Combine(AppContext.BaseDirectory, "agent-helper", AgentPipe.HelperExecutableName);
var agentToken = builder.Configuration["AgentSeat:AgentToken"] ??
                 Environment.GetEnvironmentVariable("AGENTSEAT_AGENT_TOKEN");
var agentTokenFile = builder.Configuration["AgentSeat:AgentTokenFile"] ??
                     Environment.GetEnvironmentVariable("AGENTSEAT_AGENT_TOKEN_FILE") ??
                     Path.Combine(dataDirectory, "agent-token.txt");
var agentAllowlistFile = builder.Configuration["AgentSeat:AgentAllowlistFile"] ??
                         Environment.GetEnvironmentVariable("AGENTSEAT_AGENT_ALLOWLIST") ??
                         Path.Combine(dataDirectory, "agent-seats.json");
var agentShareRoot = builder.Configuration["AgentSeat:AgentShareRoot"] ??
                     Environment.GetEnvironmentVariable("AGENTSEAT_AGENT_SHARE") ??
                     Path.Combine(dataDirectory, "agent-share");
var agentAllowConsoleValue = builder.Configuration["AgentSeat:AgentAllowConsoleSession"] ??
                             Environment.GetEnvironmentVariable("AGENTSEAT_AGENT_ALLOW_CONSOLE");
var agentAllowConsole = bool.TryParse(agentAllowConsoleValue, out var parsedAllowConsole) && parsedAllowConsole;
var actionToken = builder.Configuration["AgentSeat:ActionToken"] ??
                  Environment.GetEnvironmentVariable("AGENTSEAT_ACTION_TOKEN");
var requireActionTokenValue = builder.Configuration["AgentSeat:RequireActionToken"] ??
                              Environment.GetEnvironmentVariable("AGENTSEAT_REQUIRE_ACTION_TOKEN");
var requireActionToken = bool.TryParse(requireActionTokenValue, out var parsedRequireActionToken) &&
                         parsedRequireActionToken;
if (requireActionToken && string.IsNullOrWhiteSpace(actionToken))
{
    throw new InvalidOperationException(
        "AgentSeat action-token protection is enabled, but no action token is configured.");
}

if (requireActionToken && actionToken!.Length < 24)
{
    throw new InvalidOperationException("AgentSeat action token must contain at least 24 characters.");
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton<ISeatStore>(_ => new JsonSeatStore(dataFile));
builder.Services.AddSingleton<IWindowsSessionService, WtsSessionService>();
builder.Services.AddSingleton<IHostInspector, WindowsHostInspector>();
builder.Services.AddSingleton<SeatManager>();
builder.Services.AddSingleton<ISessionProcessLauncher, SessionProcessLauncher>();
builder.Services.AddSingleton(new SunshineStreamingOptions(
    sunshineTemplate,
    sunshineStateDirectory,
    TimeSpan.FromSeconds(20),
    steamLauncher,
    appCompatDirectory));
builder.Services.AddSingleton<ISeatStreamingService, DisabledStreamingService>();
builder.Services.AddSingleton<ISeatAnchorService, RdpAnchorScheduledTaskService>();
builder.Services.AddSingleton(new ActionTokenPolicy(requireActionToken, actionToken));
builder.Services.AddSingleton<IAgentFileShare>(new AgentFileShare(new AgentFileShareOptions(Path.GetFullPath(agentShareRoot))));
builder.Services.AddSingleton<IAgentSeatPolicy>(new AgentSeatPolicy(
    new AgentSeatPolicyOptions(Path.GetFullPath(agentAllowlistFile)) { AllowConsoleSession = agentAllowConsole }));
builder.Services.AddSingleton(new AgentTokenProvider(agentToken, Path.GetFullPath(agentTokenFile)));
builder.Services.AddSingleton(new AgentHelperHostOptions(agentHelper));
builder.Services.AddSingleton<AgentAuditLog>();
builder.Services.AddSingleton<AgentViewerSessions>();
builder.Services.AddSingleton<IAgentHelperHost, WindowsAgentHelperHost>();
builder.Services.AddSingleton<IAgentControlService, AgentControlService>();
// agent-seat never owns the host's gaming streams or device isolation settings.

var app = builder.Build();
app.Logger.LogInformation("AgentSeat data file: {DataFile}", Path.GetFullPath(dataFile));
app.Logger.LogInformation("AgentSeat management endpoint: {ListenUrl}", listenUrl);
app.Logger.LogInformation("AgentSeat Sunshine template: {Template}", Path.GetFullPath(sunshineTemplate));
app.Logger.LogInformation("AgentSeat Steam launcher: {Launcher}", Path.GetFullPath(steamLauncher));
app.Logger.LogInformation("AgentSeat AppCompat runtime: {AppCompat}", Path.GetFullPath(appCompatDirectory));
app.Logger.LogInformation(
    "AgentSeat agent control: allow list {Allowlist}, console session {Console}",
    Path.GetFullPath(agentAllowlistFile),
    agentAllowConsole ? "ALLOWED (testing override)" : "refused");
app.Logger.LogInformation(
    "AgentSeat agent control: helper {Helper}, token {TokenState}",
    Path.GetFullPath(agentHelper),
    app.Services.GetRequiredService<AgentTokenProvider>().IsConfigured ? "configured" : "not configured (agent API off)");

// The listener is loopback-only, but a web page can still reach it through DNS rebinding (a hostile name that
// resolves to 127.0.0.1). Those requests carry the attacker's Host header, so only loopback names are served.
app.Use(async (context, next) =>
{
    var requestHost = context.Request.Host.Host;
    if (requestHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        requestHost is "127.0.0.1" or "::1" or "[::1]")
    {
        await next();
        return;
    }

    context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
    await context.Response.WriteAsJsonAsync(new { error = "AgentSeat answers only to localhost, 127.0.0.1 or ::1." });
});

app.UseDefaultFiles();
// The UI is updated in place by deployments; make browsers revalidate (cheap, ETag) so an open tab picks up a new
// app.js on its next reload instead of running a stale copy from the heuristic cache.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

var api = app.MapGroup("/api/v1");

api.MapGet("/health", (AgentTokenProvider agentTokens) => Results.Ok(new
{
    status = "ok",
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev",
    hostName = Environment.MachineName,
    mode = "agent-seat",
    agentOnly = true,
    managementScope = "loopback-only",
    sessionActionsEnabled = true,
    streamingActionsEnabled = false,
    actionTokenRequired = requireActionToken,
    agentControlConfigured = agentTokens.IsConfigured
}));

api.MapGet("/about", () => Results.Ok(new
{
    product = "agent-seat",
    mode = "agent-seat",
    capability = "Independent Windows desktops for AI, with local screen viewing and direct mouse/keyboard control.",
    limitation = "The hidden local RDP anchor must remain connected; Windows Terminal Services is shared with other installed seat applications."
}));

api.MapGet("/sessions", async (IWindowsSessionService sessions, CancellationToken cancellationToken) =>
    Results.Ok(await sessions.GetSessionsAsync(cancellationToken)));

api.MapGet("/seats", async (
    SeatManager manager,
    ISeatStreamingService streaming,
    CancellationToken cancellationToken) =>
{
    var views = await manager.GetViewsAsync(cancellationToken);
    var statuses = await Task.WhenAll(views.Select(view =>
        streaming.GetStatusAsync(view.Seat, cancellationToken)));
    return Results.Ok(views.Select((view, index) => new
    {
        view.Seat,
        view.Sessions,
        view.Online,
        Streaming = statuses[index]
    }));
});

api.MapGet("/seats/{id}", async (string id, SeatManager manager, CancellationToken cancellationToken) =>
{
    var seat = await manager.GetAsync(id, cancellationToken);
    return seat is null ? Results.NotFound() : Results.Ok(seat);
});

api.MapPost("/seats", async (SeatRequest request, SeatManager manager, CancellationToken cancellationToken) =>
{
    try
    {
        var seat = await manager.CreateAsync(request.ToSeatDefinition(), cancellationToken);
        return Results.Created($"/api/v1/seats/{Uri.EscapeDataString(seat.Id)}", seat);
    }
    catch (Exception exception) when (exception is SeatValidationException or KeyNotFoundException)
    {
        return ToApiError(exception);
    }
});

api.MapPut("/seats/{id}", async (
    string id,
    SeatRequest request,
    SeatManager manager,
    ISeatStreamingService streaming,
    CancellationToken cancellationToken) =>
{
    try
    {
        var existing = await manager.GetAsync(id, cancellationToken);
        if (existing is not null)
        {
            var status = await streaming.GetStatusAsync(existing, cancellationToken);
            if (status.ProcessId is not null)
            {
                return Results.Conflict(new { error = "Stop Sunshine before changing this seat." });
            }

            if (status.State == SunshineRuntimeState.Faulted)
            {
                _ = await streaming.StopAsync(existing, cancellationToken);
            }
        }

        return Results.Ok(await manager.UpdateAsync(
            id,
            request.ToSeatDefinition(id, existing?.AgentControlEnabled ?? false),
            cancellationToken));
    }
    catch (Exception exception) when (exception is SeatValidationException or KeyNotFoundException)
    {
        return ToApiError(exception);
    }
});

// Opting a seat in or out of AI agent control is a management action. The agent token alone cannot
// do it, and even an opted-in seat is unusable without that token.
api.MapPut("/seats/{id}/agent-control", async (
    string id,
    AgentControlRequest request,
    SeatManager manager,
    CancellationToken cancellationToken) =>
{
    if (requireActionToken && !ActionAuthorized(actionToken, request.ActionToken))
    {
        return Results.Problem(
            "Seat actions are disabled or the action token is invalid.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        var seat = await manager.GetAsync(id, cancellationToken) ??
                   throw new KeyNotFoundException($"Seat '{id}' was not found.");
        return Results.Ok(await manager.UpdateAsync(
            id,
            seat with { AgentControlEnabled = request.Enabled },
            cancellationToken));
    }
    catch (Exception exception) when (exception is SeatValidationException or KeyNotFoundException)
    {
        return ToApiError(exception);
    }
});

// Streaming on/off for one seat. Off stops the seat's Sunshine first (whoever is connected through Moonlight is cut
// off) and keeps it from starting again; on lets it start by itself as soon as the seat's session is up.
api.MapPut("/seats/{id}/streaming", async (
    string id,
    StreamingToggleRequest request,
    SeatManager manager,
    ISeatStreamingService streaming,
    CancellationToken cancellationToken) =>
{
    if (requireActionToken && !ActionAuthorized(actionToken, request.ActionToken))
    {
        return Results.Problem(
            "Streaming actions are disabled or the action token is invalid.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        var seat = await manager.GetAsync(id, cancellationToken) ??
                   throw new KeyNotFoundException($"Seat '{id}' was not found.");
        if (!request.Enabled)
        {
            _ = await streaming.StopAsync(seat, cancellationToken);
        }

        return Results.Ok(await manager.UpdateAsync(
            id,
            seat with { StreamingEnabled = request.Enabled, AutoStartStreaming = request.Enabled },
            cancellationToken));
    }
    catch (Exception exception) when (IsStreamingRequestError(exception))
    {
        return ToApiError(exception);
    }
});

api.MapDelete("/seats/{id}", async (
    string id,
    SeatManager manager,
    ISeatStreamingService streaming,
    CancellationToken cancellationToken) =>
{
    var seat = await manager.GetAsync(id, cancellationToken);
    if (seat is null)
    {
        return Results.NotFound();
    }

    var status = await streaming.GetStatusAsync(seat, cancellationToken);
    if (status.ProcessId is not null)
    {
        return Results.Conflict(new { error = "Stop Sunshine before deleting this seat." });
    }

    if (status.State == SunshineRuntimeState.Faulted)
    {
        _ = await streaming.StopAsync(seat, cancellationToken);
    }

    return await manager.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
});

api.MapGet("/seats/{id}/rdp", async (string id, SeatManager manager, CancellationToken cancellationToken) =>
{
    var seat = await manager.GetAsync(id, cancellationToken);
    if (seat is null)
    {
        return Results.NotFound();
    }

    var fileName = $"agent-seat-{seat.Id}.rdp";
    return Results.File(RdpProfileBuilder.BuildUnicodeFile(seat), "application/x-rdp", fileName);
});

api.MapGet("/seats/{id}/stream", async (
    string id,
    SeatManager manager,
    ISeatStreamingService streaming,
    CancellationToken cancellationToken) =>
{
    var seat = await manager.GetAsync(id, cancellationToken);
    return seat is null
        ? Results.NotFound()
        : Results.Ok(await streaming.GetStatusAsync(seat, cancellationToken));
});

api.MapPost("/seats/{id}/stream/start", async (
    string id,
    StreamStartRequest request,
    SeatManager manager,
    ISeatStreamingService streaming,
    CancellationToken cancellationToken) =>
{
    if (requireActionToken && !ActionAuthorized(actionToken, request.ActionToken))
    {
        return Results.Problem(
            "Streaming actions are disabled or the action token is invalid.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        var seat = await manager.GetAsync(id, cancellationToken) ??
                   throw new KeyNotFoundException($"Seat '{id}' was not found.");
        var session = await manager.RequireOwnedSessionAsync(id, request.SessionId, cancellationToken);
        var status = await streaming.StartAsync(seat, session, cancellationToken);
        return status.State == SunshineRuntimeState.Ready
            ? Results.Ok(status)
            : Results.Accepted(value: status);
    }
    catch (Exception exception) when (IsStreamingRequestError(exception))
    {
        return ToApiError(exception);
    }
});

api.MapPost("/seats/{id}/stream/stop", async (
    string id,
    ConfirmStreamRequest request,
    SeatManager manager,
    ISeatStreamingService streaming,
    CancellationToken cancellationToken) =>
{
    if (requireActionToken && !ActionAuthorized(actionToken, request.ActionToken))
    {
        return Results.Problem(
            "Streaming actions are disabled or the action token is invalid.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (!request.Confirm)
    {
        return Results.BadRequest(new { error = "Set confirm=true to stop the seat's Sunshine process." });
    }

    try
    {
        var seat = await manager.GetAsync(id, cancellationToken) ??
                   throw new KeyNotFoundException($"Seat '{id}' was not found.");
        return Results.Ok(await streaming.StopAsync(seat, cancellationToken));
    }
    catch (Exception exception) when (IsStreamingRequestError(exception))
    {
        return ToApiError(exception);
    }
});

api.MapPost("/seats/{id}/stream/pair", async (
    string id,
    SunshinePairRequest request,
    SeatManager manager,
    ISeatStreamingService streaming,
    CancellationToken cancellationToken) =>
{
    if (requireActionToken && !ActionAuthorized(actionToken, request.ActionToken))
    {
        return Results.Problem(
            "Streaming actions are disabled or the action token is invalid.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        var seat = await manager.GetAsync(id, cancellationToken) ??
                   throw new KeyNotFoundException($"Seat '{id}' was not found.");
        var result = await streaming.PairAsync(seat, request.Pin, request.ClientName, cancellationToken);
        return result.Accepted ? Results.Ok(result) : Results.Conflict(result);
    }
    catch (Exception exception) when (IsStreamingRequestError(exception))
    {
        return ToApiError(exception);
    }
});

api.MapPost("/seats/{id}/start", async (
    string id,
    SeatStartRequest request,
    SeatManager manager,
    ISeatAnchorService anchors,
    CancellationToken cancellationToken) =>
{
    if (requireActionToken && !ActionAuthorized(actionToken, request.ActionToken))
    {
        return Results.Problem(
            "Seat actions are disabled or the action token is invalid.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        var seat = await manager.GetAsync(id, cancellationToken) ??
                   throw new KeyNotFoundException($"Seat '{id}' was not found.");
        if (!seat.Enabled)
        {
            throw new SeatValidationException($"Seat '{id}' is disabled.");
        }

        await anchors.StartAsync(seat, cancellationToken);
        return Results.Accepted(value: new { seatId = seat.Id, action = "start" });
    }
    catch (Exception exception) when (exception is
        SeatValidationException or
        KeyNotFoundException or
        InvalidOperationException or
        UnauthorizedAccessException or
        COMException or
        TargetInvocationException)
    {
        return ToApiError(exception);
    }
});

api.MapPost("/seats/{id}/disconnect", async (
    string id,
    ConfirmSessionRequest request,
    SeatManager manager,
    IWindowsSessionService sessions,
    CancellationToken cancellationToken) =>
{
    if (requireActionToken && !ActionAuthorized(actionToken, request.ActionToken))
    {
        return Results.Problem(
            "Session actions are disabled or the action token is invalid.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (!request.Confirm)
    {
        return Results.BadRequest(new { error = "Set confirm=true to disconnect a live session." });
    }

    try
    {
        _ = await manager.RequireOwnedSessionAsync(id, request.SessionId, cancellationToken);
        await sessions.DisconnectAsync(request.SessionId, cancellationToken);
        return Results.Accepted(value: new { request.SessionId, action = "disconnect" });
    }
    catch (Exception exception) when (exception is SeatValidationException or KeyNotFoundException)
    {
        return ToApiError(exception);
    }
});

api.MapPost("/seats/{id}/logoff", async (
    string id,
    ConfirmSessionRequest request,
    SeatManager manager,
    ISeatStreamingService streaming,
    ISeatAnchorService anchors,
    IWindowsSessionService sessions,
    CancellationToken cancellationToken) =>
{
    if (requireActionToken && !ActionAuthorized(actionToken, request.ActionToken))
    {
        return Results.Problem(
            "Session actions are disabled or the action token is invalid.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (!request.Confirm)
    {
        return Results.BadRequest(new { error = "Set confirm=true to log off a live session." });
    }

    try
    {
        var seat = await manager.GetAsync(id, cancellationToken) ??
                   throw new KeyNotFoundException($"Seat '{id}' was not found.");
        _ = await manager.RequireOwnedSessionAsync(id, request.SessionId, cancellationToken);
        _ = await anchors.StopAsync(seat, cancellationToken);
        _ = await streaming.StopAsync(seat, cancellationToken);
        await sessions.LogoffAsync(request.SessionId, cancellationToken);
        return Results.Accepted(value: new
        {
            request.SessionId,
            action = "logoff",
            automaticRestart = false
        });
    }
    catch (Exception exception) when (exception is
        SeatValidationException or
        KeyNotFoundException or
        InvalidOperationException or
        UnauthorizedAccessException or
        COMException or
        TargetInvocationException)
    {
        return ToApiError(exception);
    }
});

api.MapGet("/preflight", async (
    ISeatStore store,
    IHostInspector inspector,
    CancellationToken cancellationToken) =>
{
    var seats = await store.GetAllAsync(cancellationToken);
    return Results.Ok(await inspector.InspectAsync(seats, cancellationToken));
});

api.MapAgentEndpoints();
api.MapAgentOwnerEndpoints();

app.MapFallbackToFile("index.html");
await app.RunAsync();

static IResult ToApiError(Exception exception) => exception switch
{
    KeyNotFoundException => Results.NotFound(new { error = exception.Message }),
    _ => Results.BadRequest(new { error = exception.Message })
};

static bool IsStreamingRequestError(Exception exception) => exception is
    SeatValidationException or
    KeyNotFoundException or
    ArgumentException or
    InvalidOperationException or
    IOException or
    UnauthorizedAccessException or
    Win32Exception or
    CryptographicException;

static bool ActionAuthorized(string? configuredToken, string? suppliedToken)
{
    if (configuredToken is null || suppliedToken is null)
    {
        return false;
    }

    var expected = Encoding.UTF8.GetBytes(configuredToken);
    var supplied = Encoding.UTF8.GetBytes(suppliedToken);
    return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
}

static void EnsureLoopbackOnly(string urls)
{
    foreach (var value in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Host, "[::1]", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Management URL '{value}' is not loopback-only. AgentSeat intentionally refuses remote control bindings.");
        }
    }
}

internal sealed record SeatRequest(
    string Id,
    string DisplayName,
    string UserName,
    string HostAddress,
    int RdpPort = 3389,
    int Width = 1920,
    int Height = 1080,
    bool FullScreen = true,
    bool PlayAudioOnClient = true,
    bool RedirectClipboard = false,
    bool StreamingEnabled = true,
    bool AutoStartStreaming = true,
    int SunshineBasePort = 0,
    bool Enabled = true,
    bool? AgentControlEnabled = null)
{
    /// <param name="forcedId">Replaces the body's id (PUT uses the route id).</param>
    /// <param name="existingAgentControl">Kept when the body does not mention agent control, so a seat edit never silently revokes it.</param>
    internal SeatDefinition ToSeatDefinition(string? forcedId = null, bool existingAgentControl = false) => new()
    {
        AgentControlEnabled = AgentControlEnabled ?? existingAgentControl,
        Id = forcedId ?? Id,
        DisplayName = DisplayName,
        UserName = UserName,
        HostAddress = HostAddress,
        RdpPort = RdpPort,
        Width = Width,
        Height = Height,
        FullScreen = FullScreen,
        PlayAudioOnClient = PlayAudioOnClient,
        RedirectClipboard = RedirectClipboard,
        StreamingEnabled = StreamingEnabled,
        AutoStartStreaming = AutoStartStreaming,
        SunshineBasePort = SunshineBasePort,
        Enabled = Enabled
    };
}

internal sealed record AgentControlRequest(bool Enabled, string? ActionToken);

internal sealed record StreamingToggleRequest(bool Enabled, string? ActionToken);

internal sealed record ConfirmSessionRequest(int SessionId, bool Confirm, string? ActionToken);

internal sealed record SeatStartRequest(string? ActionToken);

internal sealed record StreamStartRequest(int SessionId, string? ActionToken);

internal sealed record ConfirmStreamRequest(bool Confirm, string? ActionToken);

internal sealed record SunshinePairRequest(string Pin, string ClientName, string? ActionToken);
