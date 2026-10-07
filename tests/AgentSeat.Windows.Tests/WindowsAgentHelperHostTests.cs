using System.Diagnostics;
using System.IO.Pipes;
using AgentSeat.Core.Agent;
using AgentSeat.Core.Models;
using AgentSeat.Windows;

namespace AgentSeat.Windows.Tests;

/// <summary>
/// Exercises the real named-pipe client against an in-process server. The service is normally
/// LocalSystem and the helper a low-privilege user, so the client must refuse a pipe that is not
/// served by the exact helper process it verified.
/// </summary>
public sealed class WindowsAgentHelperHostTests
{
    private static SeatDefinition NewSeat() => new()
    {
        Id = "t" + Guid.NewGuid().ToString("N")[..16],
        DisplayName = "Test",
        UserName = "seat-test",
        HostAddress = "pc",
        AgentControlEnabled = true
    };

    private static WindowsAgentHelperHost NewHost() =>
        new(new NeverLauncher(), new AgentHelperHostOptions(Path.Combine(AppContext.BaseDirectory, "AgentSeat.AgentHelper.exe"))
        {
            ConnectTimeout = TimeSpan.FromSeconds(2)
        });

    private static AgentHelperInfo ThisProcess()
    {
        using var self = Process.GetCurrentProcess();
        return new AgentHelperInfo(self.Id, self.SessionId, new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero));
    }

    private static async Task ServeOnceAsync(string pipeName, Func<AgentRequest, AgentResponse> handler, CancellationToken cancellationToken)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(cancellationToken);
        var request = await AgentFraming.ReadJsonAsync<AgentRequest>(server, cancellationToken);
        if (request is null)
        {
            return; // The client hung up without sending anything.
        }

        await AgentFraming.WriteJsonAsync(server, handler(request), cancellationToken);
        server.WaitForPipeDrain();
    }

    [Fact]
    public async Task SendsARequestAndReadsTheResponse()
    {
        var seat = NewSeat();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeOnceAsync(
            AgentPipe.NameFor(seat.Id),
            request => AgentResponse.Success(new System.Text.Json.Nodes.JsonObject { ["echo"] = request.Action, ["x"] = request.X }),
            cancellation.Token);

        var response = await NewHost().SendAsync(
            seat,
            ThisProcess(),
            new AgentRequest { Action = AgentActions.MouseMove, X = 12, Y = 34 },
            cancellation.Token);
        await server;

        Assert.True(response.Ok);
        Assert.Equal(AgentActions.MouseMove, response.Data!["echo"]!.GetValue<string>());
        Assert.Equal(12, response.Data["x"]!.GetValue<int>());
    }

    [Fact]
    public async Task RefusesAPipeServedByADifferentProcessThanTheVerifiedHelper()
    {
        var seat = NewSeat();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // The "helper" the service believes in is a real, unrelated process; the pipe is served by this test.
        using var impostorTarget = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 20 127.0.0.1 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        try
        {
            var claimed = new AgentHelperInfo(
                impostorTarget.Id,
                impostorTarget.SessionId,
                new DateTimeOffset(impostorTarget.StartTime.ToUniversalTime(), TimeSpan.Zero));
            var received = false;
            var server = ServeOnceAsync(
                AgentPipe.NameFor(seat.Id),
                request =>
                {
                    received = true;
                    return AgentResponse.Success();
                },
                cancellation.Token);

            var exception = await Assert.ThrowsAsync<AgentControlException>(() => NewHost().SendAsync(
                seat,
                claimed,
                new AgentRequest { Action = AgentActions.Info },
                cancellation.Token));

            Assert.Equal(AgentControlException.HelperUnavailable, exception.Code);
            Assert.Contains("not served by the verified helper", exception.Message, StringComparison.Ordinal);
            cancellation.Cancel();
            await Task.WhenAny(server, Task.Delay(500));
            Assert.False(received, "No command may reach a pipe whose server is not the verified helper.");
        }
        finally
        {
            impostorTarget.Kill();
        }
    }

    [Fact]
    public async Task DeadHelperFailsImmediatelyInsteadOfWaitingForThePipe()
    {
        var seat = NewSeat();
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        var info = new AgentHelperInfo(gone.Id, gone.SessionId, new DateTimeOffset(gone.StartTime.ToUniversalTime(), TimeSpan.Zero));
        await gone.WaitForExitAsync();
        var watch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<AgentControlException>(() => NewHost().SendAsync(
            seat,
            info,
            new AgentRequest { Action = AgentActions.Info },
            CancellationToken.None));

        Assert.Equal(AgentControlException.HelperUnavailable, exception.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task ServerHangingUpMidExchangeIsAFailureNotARetryableOutage()
    {
        var seat = NewSeat();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(
                AgentPipe.NameFor(seat.Id),
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cancellation.Token);
            _ = await AgentFraming.ReadAsync(pipe, cancellation.Token);
            // Close without answering.
        });

        var exception = await Assert.ThrowsAsync<AgentControlException>(() => NewHost().SendAsync(
            seat,
            ThisProcess(),
            new AgentRequest { Action = AgentActions.Click, X = 1, Y = 1 },
            cancellation.Token));
        await server;

        Assert.Equal(AgentControlException.HelperFailed, exception.Code);
    }

    [Fact]
    public void ProcessIdentityIsCheckedByStartTime()
    {
        var host = NewHost();
        var real = ThisProcess();

        Assert.True(host.IsRunning(real));
        Assert.False(host.IsRunning(real with { StartedUtc = real.StartedUtc.AddMinutes(-10) }));
        Assert.False(host.IsRunning(real with { ProcessId = 1 << 22 }));
    }

    private sealed class NeverLauncher : ISessionProcessLauncher
    {
        public string GetUserProfilePath(int sessionId) => throw new NotSupportedException();

        public SessionProcessStartResult Start(int sessionId, string executablePath, IReadOnlyList<string> arguments, string workingDirectory) =>
            throw new NotSupportedException();
    }
}
