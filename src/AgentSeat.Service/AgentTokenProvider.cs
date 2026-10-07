using System.Security.Cryptography;
using System.Text;

namespace AgentSeat.Service;

/// <summary>
/// Holds the bearer token that guards the agent API. A token from configuration or the
/// environment wins; otherwise it is read from a file (re-read when the file changes, so
/// rotating it needs no service restart). With no token the agent API stays switched off.
/// </summary>
internal sealed class AgentTokenProvider(string? configuredToken, string tokenFilePath)
{
    internal const int MinimumLength = 24;

    private readonly object _gate = new();
    private DateTime _fileStamp;
    private long _fileLength = -1;
    private string? _fileToken;

    internal string TokenFilePath { get; } = tokenFilePath;

    internal bool IsConfigured => Current() is not null;
    internal string? Fingerprint => Current() is { } token
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) : null;

    internal bool Authorize(string? presented)
    {
        var expected = Current();
        if (expected is null || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        return expectedBytes.Length == presentedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }

    internal static string? ReadBearer(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        return authorizationHeader is not null &&
               authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[prefix.Length..].Trim()
            : null;
    }

    private string? Current()
    {
        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            return configuredToken.Trim().Length >= MinimumLength ? configuredToken.Trim() : null;
        }

        lock (_gate)
        {
            try
            {
                var info = new FileInfo(TokenFilePath);
                if (!info.Exists)
                {
                    _fileToken = null;
                    _fileLength = -1;
                    return null;
                }

                if (info.LastWriteTimeUtc != _fileStamp || info.Length != _fileLength)
                {
                    var text = File.ReadAllText(TokenFilePath).Trim();
                    _fileToken = text.Length >= MinimumLength ? text : null;
                    _fileStamp = info.LastWriteTimeUtc;
                    _fileLength = info.Length;
                }

                return _fileToken;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return _fileToken;
            }
        }
    }
}
