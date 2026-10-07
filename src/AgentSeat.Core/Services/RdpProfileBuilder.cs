using System.Text;
using AgentSeat.Core.Models;

namespace AgentSeat.Core.Services;

public static class RdpProfileBuilder
{
    public static string Build(SeatDefinition seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        RejectLineBreaks(seat.HostAddress, nameof(seat.HostAddress));
        RejectLineBreaks(seat.UserName, nameof(seat.UserName));

        var host = FormatHost(seat.HostAddress);
        var userName = seat.UserName.Contains('\\', StringComparison.Ordinal) ||
                       seat.UserName.Contains('@', StringComparison.Ordinal)
            ? seat.UserName
            : $".\\{seat.UserName}";

        var lines = new[]
        {
            $"screen mode id:i:{(seat.FullScreen ? 2 : 1)}",
            "use multimon:i:0",
            $"desktopwidth:i:{seat.Width}",
            $"desktopheight:i:{seat.Height}",
            "session bpp:i:32",
            "compression:i:1",
            "keyboardhook:i:2",
            "networkautodetect:i:1",
            "bandwidthautodetect:i:1",
            "connection type:i:7",
            "disable wallpaper:i:0",
            "allow font smoothing:i:1",
            "allow desktop composition:i:1",
            "bitmapcachepersistenable:i:1",
            "videoplaybackmode:i:1",
            "displayconnectionbar:i:1",
            "remoteapplicationmode:i:0",
            $"audiomode:i:{(seat.PlayAudioOnClient ? 0 : 2)}",
            "audiocapturemode:i:0",
            $"redirectclipboard:i:{(seat.RedirectClipboard ? 1 : 0)}",
            "redirectprinters:i:0",
            "redirectcomports:i:0",
            "redirectsmartcards:i:0",
            "redirectwebauthn:i:0",
            "redirectlocation:i:0",
            "redirectdrives:i:0",
            "drivestoredirect:s:",
            "autoreconnection enabled:i:1",
            "prompt for credentials:i:1",
            "authentication level:i:2",
            "negotiate security layer:i:1",
            "enablecredsspsupport:i:1",
            "gatewayusagemethod:i:0",
            $"full address:s:{host}:{seat.RdpPort}",
            $"username:s:{userName}"
        };

        return string.Join("\r\n", lines) + "\r\n";
    }

    public static byte[] BuildUnicodeFile(SeatDefinition seat)
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        return encoding.GetPreamble().Concat(encoding.GetBytes(Build(seat))).ToArray();
    }

    private static string FormatHost(string host)
    {
        var trimmed = host.Trim();
        return trimmed.Contains(':', StringComparison.Ordinal) &&
               !(trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            ? $"[{trimmed}]"
            : trimmed;
    }

    private static void RejectLineBreaks(string value, string parameterName)
    {
        if (value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("RDP fields cannot contain line breaks.", parameterName);
        }
    }
}
