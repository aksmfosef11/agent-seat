using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSeat.Core.Abstractions;
using AgentSeat.Core.Agent;
using AgentSeat.Core.Models;
using AgentSeat.Core.Services;

namespace AgentSeat.Service;

/// <summary>
/// The agent API: <c>/api/v1/seats/{id}/agent/*</c>. Every route needs the agent bearer token (a
/// separate secret from the management action token), the seat must have opted in, and the batch
/// format is OpenAI computer-use <c>actions[]</c>, so any model harness or the bundled CLI can drive it.
/// </summary>
internal static class AgentEndpoints
{
    internal static void MapAgentEndpoints(this RouteGroupBuilder api)
    {
        var agent = api.MapGroup("/seats/{id}/agent");
        agent.AddEndpointFilter(async (context, next) =>
        {
            var tokens = context.HttpContext.RequestServices.GetRequiredService<AgentTokenProvider>();
            if (!tokens.IsConfigured)
            {
                return Failure(
                    StatusCodes.Status503ServiceUnavailable,
                    "agent_disabled",
                    $"Agent control is switched off: no agent token is configured (AgentSeat:AgentToken, AGENTSEAT_AGENT_TOKEN or {tokens.TokenFilePath}).");
            }

            var presented = AgentTokenProvider.ReadBearer(context.HttpContext.Request.Headers.Authorization);
            var viewer = context.HttpContext.RequestServices.GetRequiredService<AgentViewerSessions>();
            var seatId = context.HttpContext.Request.RouteValues["id"]?.ToString() ?? "";
            return tokens.Authorize(presented) || viewer.Authorize(
                context.HttpContext.Request.Cookies[AgentViewerSessions.CookieName], seatId, tokens.Fingerprint)
                ? await next(context)
                : Failure(StatusCodes.Status401Unauthorized, "unauthorized", "Missing or invalid agent token.");
        });

        agent.MapPost("/viewer-ticket", async (string id, HttpContext context, AgentTokenProvider tokens,
            AgentViewerSessions viewer, SeatManager manager, IAgentControlService control, CancellationToken cancellationToken) =>
        {
            // A browser session cannot mint new tickets; issuance requires the protected CLI bearer token.
            var presentedToken = AgentTokenProvider.ReadBearer(context.Request.Headers.Authorization);
            if (!tokens.Authorize(presentedToken))
                return Failure(StatusCodes.Status401Unauthorized, "unauthorized", "Bearer token required.");
            // Bind issuance to the credential that was authorized, even if the owner rotates it during the status read.
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presentedToken!)));
            var seat = await manager.GetAsync(id, cancellationToken);
            if (seat is null) return SeatNotFound(id);
            var status = await control.GetStatusAsync(seat, cancellationToken);
            if (status.Refused) return Failure(StatusCodes.Status403Forbidden, "disabled", status.Detail);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { ticket = viewer.Issue(id, fingerprint), expiresIn = 60 });
        });

        api.MapGet("/agent-viewer", (string? ticket, HttpContext context, AgentViewerSessions viewer, AgentTokenProvider tokens) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var session = viewer.Redeem(ticket, tokens.Fingerprint);
            if (session is null) return Failure(StatusCodes.Status401Unauthorized, "expired_ticket", "Viewer link expired. Run computer view again.");
            context.Response.Cookies.Append(AgentViewerSessions.CookieName, session.Cookie, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromMinutes(30),
                Path = $"/api/v1/seats/{Uri.EscapeDataString(session.Seat)}/agent"
            });
            return Results.Redirect($"/?viewer={Uri.EscapeDataString(session.Seat)}");
        });

        agent.MapGet("/status", async (
            string id,
            SeatManager manager,
            IAgentControlService control,
            CancellationToken cancellationToken) =>
        {
            var seat = await manager.GetAsync(id, cancellationToken);
            return seat is null ? SeatNotFound(id) : Results.Json(await control.GetStatusAsync(seat, cancellationToken), AgentJson.Options);
        });

        agent.MapPost("/start", (string id, SeatManager manager, IAgentControlService control, CancellationToken cancellationToken) =>
            WithSeat(id, manager, cancellationToken, async seat =>
                Results.Json(await control.StartAsync(seat, cancellationToken), AgentJson.Options)));

        // Lifecycle and file hand-off that the agent may do for itself. Both refuse while a batch is running, while
        // paused, and for the physical console, and neither can reach anything outside the seat or its share folder.
        agent.MapPost("/close", (
            string id,
            AgentCloseRequest? request,
            SeatManager manager,
            IAgentControlService control,
            ISeatStreamingService streaming,
            CancellationToken cancellationToken) =>
            WithSeat(id, manager, cancellationToken, async seat =>
            {
                // A seat that is streaming may have a person playing on it through Moonlight; logging it off would
                // throw them out. Only the owner ends that (stop the stream or log the session off in the UI).
                if ((await streaming.GetStatusAsync(seat, cancellationToken)).ProcessId is not null)
                {
                    return Failure(
                        StatusCodes.Status409Conflict,
                        "streaming",
                        $"Seat '{seat.Id}' is streaming (Sunshine is running), so someone may be connected to it. Ask the user to stop its stream first.");
                }

                return Results.Json(
                    await control.CloseAsync(seat, wipeFiles: request?.KeepFiles != true, cancellationToken),
                    AgentJson.Options);
            }));

        agent.MapPost("/files/clean", (
            string id,
            SeatManager manager,
            IAgentControlService control,
            CancellationToken cancellationToken) =>
            WithSeat(id, manager, cancellationToken, async seat =>
                Results.Json(await control.CleanFilesAsync(seat, cancellationToken), AgentJson.Options)));

        // Pause, resume and the kill switch are deliberately NOT here: the agent token must not be able to undo
        // the owner's controls. They live on the management side (MapAgentOwnerEndpoints).
        agent.MapGet("/log", (string id, int? count, SeatManager manager, IAgentControlService control, CancellationToken cancellationToken) =>
            WithSeat(id, manager, cancellationToken, seat =>
                Task.FromResult(Results.Json(control.GetLog(seat, Math.Clamp(count ?? 50, 1, 500)), AgentJson.Options))));

        agent.MapPost("/actions", async (
            string id,
            HttpRequest request,
            SeatManager manager,
            IAgentControlService control,
            CancellationToken cancellationToken) =>
        {
            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken);
            }
            catch (JsonException exception)
            {
                return Failure(StatusCodes.Status400BadRequest, AgentControlException.InvalidRequest, $"The body is not valid JSON: {exception.Message}");
            }

            if (!AgentActionBatch.TryParse(body, out var batch, out var parseError))
            {
                return Failure(StatusCodes.Status400BadRequest, AgentControlException.InvalidRequest, parseError!);
            }

            return await WithSeat(id, manager, cancellationToken, async seat =>
            {
                var result = await control.ExecuteBatchAsync(seat, batch!, cancellationToken);
                return Results.Json(result, AgentJson.Options, statusCode: result.Ok ? 200 : StatusFor(result.ErrorCode));
            });
        });

        agent.MapGet("/screenshot", (
            string id,
            double? scale,
            string? format,
            SeatManager manager,
            IAgentControlService control,
            HttpContext context,
            CancellationToken cancellationToken) =>
            WithSeat(id, manager, cancellationToken, async seat =>
            {
                var options = new AgentRequest { Action = AgentActions.Screenshot, Scale = scale, Format = format };
                var problem = AgentRequestValidator.Validate(options);
                if (problem is not null)
                {
                    return Failure(StatusCodes.Status400BadRequest, AgentControlException.InvalidRequest, problem);
                }

                var result = await control.ExecuteBatchAsync(
                    seat,
                    new AgentBatchRequest([], Screenshot: true, SettleMilliseconds: 0, options),
                    cancellationToken);
                if (result.Screenshot is not { } screenshot)
                {
                    return Failure(StatusFor(result.ErrorCode), result.ErrorCode ?? "screenshot_failed", result.Error ?? "No screenshot.");
                }

                // The helper is untrusted: only a real base64 image of an expected type is ever relayed.
                var bytes = new byte[(screenshot.DataBase64.Length * 3 / 4) + 3];
                if (screenshot.MimeType is not ("image/png" or "image/jpeg") ||
                    !Convert.TryFromBase64String(screenshot.DataBase64, bytes, out var written))
                {
                    return Failure(StatusCodes.Status502BadGateway, AgentControlException.HelperFailed, "The helper returned an invalid screenshot.");
                }

                context.Response.Headers.CacheControl = "no-store";
                return Results.File(bytes.AsSpan(0, written).ToArray(), screenshot.MimeType);
            }));
    }

    /// <summary>
    /// The owner's controls: <c>POST /seats/{id}/agent-control/{pause|resume|stop}</c>. They are management
    /// actions (gated by the action token when that protection is on), not agent calls, so a caller that holds
    /// only the agent token cannot resume what the owner paused. Code running as the owner's own Windows user
    /// can still reach the management API; the allow list and console exclusion are what keep an agent off the
    /// owner's desktop, and pause is a cooperative stop for a well-behaved agent.
    /// </summary>
    internal static void MapAgentOwnerEndpoints(this RouteGroupBuilder api)
    {
        var owner = api.MapGroup("/seats/{id}/agent-control");

        // Read-only views for the owner's web UI, without the agent token (like GET /seats). They carry no screen
        // content: the live preview stays behind the agent token. The log holds action summaries only, never typed
        // text or launch arguments.
        owner.MapGet("/status", (string id, SeatManager manager, IAgentControlService control, CancellationToken cancellationToken) =>
            WithSeat(id, manager, cancellationToken, async seat =>
                Results.Json(await control.GetStatusAsync(seat, cancellationToken), AgentJson.Options)));
        owner.MapGet("/log", (string id, int? count, SeatManager manager, IAgentControlService control, CancellationToken cancellationToken) =>
            WithSeat(id, manager, cancellationToken, seat =>
                Task.FromResult(Results.Json(control.GetLog(seat, Math.Clamp(count ?? 50, 1, 500)), AgentJson.Options))));

        foreach (var action in new[] { "pause", "resume", "stop" })
        {
            owner.MapPost($"/{action}", (
                string id,
                AgentOwnerRequest? request,
                ActionTokenPolicy actionTokens,
                SeatManager manager,
                IAgentControlService control,
                CancellationToken cancellationToken) =>
            {
                if (!actionTokens.Authorize(request?.ActionToken))
                {
                    return Task.FromResult(Failure(
                        StatusCodes.Status403Forbidden,
                        "forbidden",
                        "Seat actions are disabled or the action token is invalid."));
                }

                return WithSeat(id, manager, cancellationToken, async seat => Results.Json(
                    action switch
                    {
                        "pause" => await control.SetPausedAsync(seat, paused: true, cancellationToken),
                        "resume" => await control.SetPausedAsync(seat, paused: false, cancellationToken),
                        _ => await control.StopAsync(seat, cancellationToken)
                    },
                    AgentJson.Options));
            });
        }
    }

    private static async Task<IResult> WithSeat(
        string id,
        SeatManager manager,
        CancellationToken cancellationToken,
        Func<SeatDefinition, Task<IResult>> action)
    {
        var seat = await manager.GetAsync(id, cancellationToken);
        if (seat is null)
        {
            return SeatNotFound(id);
        }

        try
        {
            return await action(seat);
        }
        catch (AgentControlException exception)
        {
            return Failure(StatusFor(exception.Code), exception.Code, exception.Message);
        }
    }

    private static IResult SeatNotFound(string id) =>
        Failure(StatusCodes.Status404NotFound, "seat_not_found", $"Seat '{id}' was not found.");

    private static IResult Failure(int status, string code, string message) =>
        Results.Json(new { error = message, errorCode = code }, statusCode: status);

    private static int StatusFor(string? code) => code switch
    {
        AgentControlException.InvalidRequest => StatusCodes.Status400BadRequest,
        AgentControlException.Disabled => StatusCodes.Status403Forbidden,
        AgentControlException.Paused or AgentControlException.Stopped => StatusCodes.Status423Locked,
        AgentControlException.Busy => StatusCodes.Status429TooManyRequests,
        AgentControlException.SessionUnavailable => StatusCodes.Status409Conflict,
        AgentControlException.HelperUnavailable or AgentControlException.HelperFailed => StatusCodes.Status502BadGateway,
        AgentControlException.Timeout => StatusCodes.Status504GatewayTimeout,
        // out_of_bounds, window_not_found, desktop_unavailable, screen_unavailable, action_failed
        _ => StatusCodes.Status422UnprocessableEntity
    };
}
