using System.Text;
using System.Text.RegularExpressions;

namespace AgentSeat.Core.Agent;

/// <summary>Names and command-line rules shared by the service and the session helper.</summary>
public static partial class AgentPipe
{
    public const string HelperExecutableName = "AgentSeat.AgentHelper.exe";
    public const string HelperProcessName = "AgentSeat.AgentHelper";

    /// <summary>
    /// Named pipes live in one machine-wide namespace (unlike desktops), so the seat id is part of
    /// the name. The helper's DACL, not the name, decides who may connect.
    /// </summary>
    public static string NameFor(string seatId)
    {
        if (!SeatIdPattern().IsMatch(seatId))
        {
            throw new ArgumentException("Seat id is not valid for a pipe name.", nameof(seatId));
        }

        return $"AgentSeat.Agent.{seatId}";
    }

    /// <summary>Quotes arguments the way CommandLineToArgvW expects, for <c>ProcessStartInfo.Arguments</c>.</summary>
    public static string JoinArguments(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(Quote));

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        var output = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                output.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            output.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        output.Append('\\', backslashes * 2).Append('"');
        return output.ToString();
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SeatIdPattern();
}
