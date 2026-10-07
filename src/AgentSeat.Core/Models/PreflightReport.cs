namespace AgentSeat.Core.Models;

public enum PreflightStatus
{
    Pass,
    Warning,
    Fail
}

public sealed record PreflightCheck(
    string Code,
    string Title,
    PreflightStatus Status,
    string Detail,
    string? Remediation = null);

public sealed record PreflightReport(
    DateTimeOffset CheckedAtUtc,
    string HostName,
    string OperatingSystem,
    IReadOnlyList<PreflightCheck> Checks)
{
    public bool Ready => Checks.All(check => check.Status != PreflightStatus.Fail);
}
