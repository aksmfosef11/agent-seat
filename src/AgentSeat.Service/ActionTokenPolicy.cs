using System.Security.Cryptography;
using System.Text;

namespace AgentSeat.Service;

/// <summary>The management action-token rule (<c>AgentSeat:RequireActionToken</c>) for endpoints outside Program.cs.</summary>
internal sealed record ActionTokenPolicy(bool Required, string? Token)
{
    internal bool Authorize(string? supplied)
    {
        if (!Required)
        {
            return true;
        }

        if (Token is null || supplied is null)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(Token);
        var presented = Encoding.UTF8.GetBytes(supplied);
        return expected.Length == presented.Length && CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}

internal sealed record AgentOwnerRequest(string? ActionToken);

internal sealed record AgentCloseRequest(bool? KeepFiles);
