using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using AgentSeat.Core.Agent;

namespace AgentSeat.AgentHelper;

/// <summary>Turns one validated request into an effect on this session and a response.</summary>
internal sealed class AgentDispatcher
{
    internal AgentResponse Handle(AgentRequest request)
    {
        var problem = AgentRequestValidator.Validate(request);
        if (problem is not null)
        {
            return AgentResponse.Failure(AgentControlException.InvalidRequest, problem);
        }

        try
        {
            lock (InputInjector.Sync)
            {
                // Status checks and the owner's live preview must not keep held input alive.
                var isInput = request.Action is not (AgentActions.Info or AgentActions.Screenshot or AgentActions.Windows or AgentActions.Observe);
                if (isInput)
                {
                    InputInjector.MarkActivity();
                }

                var data = Execute(request);
                // While anything is held every input action says so, so the agent does not forget to let go.
                if (isInput && data?["held"] is null && InputInjector.HeldNames().Count > 0)
                {
                    data ??= new JsonObject();
                    data["held"] = HeldArray();
                }

                return AgentResponse.Success(data);
            }
        }
        catch (AgentActionException exception)
        {
            HelperLog.Write($"{request.Action} failed ({exception.Code}): {exception.Message}");
            return AgentResponse.Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            HelperLog.Write($"{request.Action} crashed: {exception}");
            return AgentResponse.Failure("action_failed", exception.Message);
        }
    }

    private static JsonObject? Execute(AgentRequest request)
    {
        switch (request.Action)
        {
            case AgentActions.Info:
                return Info();
            case AgentActions.Screenshot:
                return ScreenCapture.Capture(request);
            case AgentActions.MouseMove:
                InputInjector.Move(request.X!.Value, request.Y!.Value);
                return Cursor();
            case AgentActions.Click:
                InputInjector.Click(request.X, request.Y, request.Button, request.Clicks ?? 1, request.Modifiers);
                return Cursor();
            case AgentActions.Drag:
                InputInjector.Drag(
                    request.Points ?? [new AgentPoint(request.X!.Value, request.Y!.Value),
                        new AgentPoint(request.EndX!.Value, request.EndY!.Value)],
                    request.Button,
                    request.Modifiers);
                return Cursor();
            case AgentActions.Scroll:
                InputInjector.Scroll(request.X, request.Y, request.Direction!.ToLowerInvariant(), request.Amount ?? 3, request.Modifiers);
                return Cursor();
            case AgentActions.Type:
                InputInjector.TypeText(request.Text!);
                return null;
            case AgentActions.Key:
                InputInjector.PressKeys(request.Keys!, request.Repeat ?? 1);
                return null;
            case AgentActions.KeyDown:
                InputInjector.KeyDown(request.Keys!);
                return Held();
            case AgentActions.KeyUp:
                InputInjector.KeyUp(request.Keys);
                return Held();
            case AgentActions.MouseDown:
                InputInjector.MouseDown(request.X, request.Y, request.Button);
                return Held();
            case AgentActions.MouseUp:
                InputInjector.MouseUp(request.X, request.Y, request.Button);
                return Held();
            case AgentActions.Hold:
                InputInjector.Hold(request.Keys, request.Button, request.X, request.Y, request.Milliseconds!.Value);
                return Held();
            case AgentActions.Release:
                _ = InputInjector.ReleaseHeld();
                return Held();
            case AgentActions.Windows:
                return WindowCatalog.List();
            case AgentActions.Observe:
                return UiObservation.Capture(request);
            case AgentActions.FocusWindow:
                return WindowCatalog.Focus(request.Handle!.Value);
            case AgentActions.CloseWindow:
                WindowCatalog.Close(request.Handle!.Value);
                return null;
            case AgentActions.Launch:
                return WindowCatalog.Launch(request);
            default:
                throw new AgentActionException(AgentControlException.InvalidRequest, $"Unsupported action '{request.Action}'.");
        }
    }

    private static JsonObject Cursor()
    {
        var (x, y) = InputInjector.CursorPosition();
        return new JsonObject { ["cursorX"] = x, ["cursorY"] = y };
    }

    /// <summary>What is still held after the command; the service tracks it so pause and stop can let go.</summary>
    private static JsonObject Held() => new() { ["held"] = HeldArray() };

    private static JsonArray HeldArray() =>
        new(InputInjector.HeldNames().Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());

    private static JsonObject Info()
    {
        var desktop = ScreenCapture.GetDesktop();
        var (cursorX, cursorY) = InputInjector.CursorPosition();
        using var self = Process.GetCurrentProcess();
        return new JsonObject
        {
            ["processId"] = self.Id,
            ["sessionId"] = self.SessionId,
            ["user"] = $"{Environment.UserDomainName}\\{Environment.UserName}",
            ["helperVersion"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev",
            ["desktopWidth"] = desktop.Width,
            ["desktopHeight"] = desktop.Height,
            ["originX"] = desktop.X,
            ["originY"] = desktop.Y,
            ["cursorX"] = cursorX,
            ["cursorY"] = cursorY,
            ["held"] = HeldArray(),
            ["foregroundWindow"] = WindowCatalog.Foreground()
        };
    }
}
