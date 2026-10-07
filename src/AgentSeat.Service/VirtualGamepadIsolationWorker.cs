using System.Security.Principal;
using AgentSeat.Core.Abstractions;
using AgentSeat.Windows;

namespace AgentSeat.Service;

/// <summary>
/// Keeps Moonlight's ViGEm controllers visible only to seat users, so the physical console's
/// Steam Input and games stop receiving a friend's gamepad. Disabling the
/// <c>AgentSeat:VirtualGamepadIsolation</c> setting restores system-wide visibility.
/// </summary>
internal sealed class VirtualGamepadIsolationWorker(
    ISeatStore store,
    VirtualGamepadIsolation isolation,
    IConfiguration configuration,
    ILogger<VirtualGamepadIsolationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SeatRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    private readonly HashSet<string> _unresolvedUsers = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsPrivileged())
        {
            logger.LogWarning(
                "Virtual gamepad isolation requires LocalSystem or an elevated administrator; Moonlight controllers remain visible to every session.");
            return;
        }

        if (!configuration.GetValue("AgentSeat:VirtualGamepadIsolation", defaultValue: true))
        {
            try
            {
                Log(isolation.Clear(), "Removed seat-only access from");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not remove virtual gamepad isolation.");
            }
            return;
        }

        string? desired = null;
        string? applied = null;
        var nextSeatRefresh = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextSeatRefresh)
                {
                    desired = await BuildDescriptorAsync(stoppingToken).ConfigureAwait(false);
                    nextSeatRefresh = DateTimeOffset.UtcNow + SeatRefreshInterval;
                }

                if (desired is null)
                {
                    continue;
                }

                // A new seat user changes the descriptor; remembered controllers are then updated too.
                var result = isolation.Apply(desired, includeDisconnected: applied != desired);
                applied = desired;
                Log(result, "Restricted Moonlight controller access to seat users on");
                if (result.Failures.Count > 0)
                {
                    await Task.Delay(FailureBackoff, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not apply virtual gamepad isolation.");
                await Task.Delay(FailureBackoff, stoppingToken).ConfigureAwait(false);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task<string?> BuildDescriptorAsync(CancellationToken cancellationToken)
    {
        var seats = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var users = new List<SecurityIdentifier>();
        foreach (var seat in seats)
        {
            try
            {
                users.Add((SecurityIdentifier)new NTAccount(seat.UserName).Translate(typeof(SecurityIdentifier)));
            }
            catch (IdentityNotMappedException)
            {
                if (_unresolvedUsers.Add(seat.UserName))
                {
                    logger.LogWarning(
                        "Seat {SeatId} user {UserName} has no Windows account; it cannot use Moonlight controllers.",
                        seat.Id,
                        seat.UserName);
                }
            }
        }

        // Without a seat user there is nobody to scope controllers to, so leave them untouched.
        return users.Count == 0 ? null : VirtualGamepadIsolation.BuildSecurityDescriptor(users);
    }

    private void Log(VirtualGamepadIsolationResult result, string action)
    {
        if (result.Updated > 0)
        {
            logger.LogInformation(
                "{Action} {Updated} ViGEm device instance(s); restarted {Restarted}, restart pending {Pending}.",
                action,
                result.Updated,
                result.Restarted,
                result.RestartPending);
        }

        foreach (var failure in result.Failures)
        {
            logger.LogWarning("Virtual gamepad isolation: {Failure}", failure);
        }
    }

    private static bool IsPrivileged()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem ||
               new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
