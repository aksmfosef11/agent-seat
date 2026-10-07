namespace AgentSeat.Core.Agent;

/// <summary>
/// One key press: modifiers held while <see cref="Keys"/> go down together (in order) and come back up (in reverse),
/// so <c>w+d</c> presses W and D at the same time, as a diagonal move in a game needs. A chord of modifiers only
/// taps them.
/// </summary>
public sealed record KeyChord(IReadOnlyList<ushort> Modifiers, IReadOnlyList<ushort> Keys)
{
    /// <summary>The last regular key of the chord, or null for a modifier-only chord.</summary>
    public ushort? Key => Keys.Count > 0 ? Keys[^1] : null;
}

/// <summary>
/// Parses human key names ("ctrl+shift+t", "Return", "pgdn") into Windows virtual-key codes. The
/// table assumes a US layout for punctuation keys; text with other characters should use the
/// <c>type</c> action, which sends Unicode characters directly.
/// </summary>
public static class AgentKeyMap
{
    public const ushort VkShift = 0x10;
    public const ushort VkControl = 0x11;
    public const ushort VkMenu = 0x12;
    public const ushort VkLeftWindows = 0x5B;
    public const ushort VkReturn = 0x0D;
    public const ushort VkTab = 0x09;

    /// <summary>Regular (non-modifier) keys one chord may press at the same time.</summary>
    public const int MaxKeysPerChord = 8;

    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = VkControl,
        ["control"] = VkControl,
        ["shift"] = VkShift,
        ["alt"] = VkMenu,
        ["option"] = VkMenu,
        ["win"] = VkLeftWindows,
        ["windows"] = VkLeftWindows,
        ["super"] = VkLeftWindows,
        ["meta"] = VkLeftWindows,
        ["cmd"] = VkLeftWindows,
        ["command"] = VkLeftWindows
    };

    private static readonly Dictionary<string, ushort> Keys = BuildKeys();

    // Declared after Keys: static initializers run in textual order.
    private static readonly Dictionary<ushort, string> Names = BuildNames();

    private static readonly HashSet<ushort> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home arrows
        0x2C, 0x2D, 0x2E, // PrintScreen Insert Delete
        0x5B, 0x5C, 0x5D, // Windows keys, Menu
        0x6F, // numpad divide
        0x90 // NumLock
    ];

    public static bool IsExtendedKey(ushort virtualKey) => ExtendedKeys.Contains(virtualKey);

    public static bool IsModifierName(string name) => Modifiers.ContainsKey(name);

    public static ushort? ResolveModifier(string name) =>
        Modifiers.TryGetValue(name, out var code) ? code : null;

    /// <summary>A readable name for a virtual-key code, used to report which keys are being held.</summary>
    public static string NameOf(ushort virtualKey) =>
        Names.TryGetValue(virtualKey, out var name)
            ? name
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"vk{virtualKey:X2}");

    /// <summary>Every key a sequence touches, modifiers first within each chord, without repeats.</summary>
    public static IReadOnlyList<ushort> KeysIn(IReadOnlyList<KeyChord> chords)
    {
        var keys = new List<ushort>();
        void Add(ushort code)
        {
            if (!keys.Contains(code))
            {
                keys.Add(code);
            }
        }

        foreach (var chord in chords)
        {
            foreach (var code in chord.Modifiers.Concat(chord.Keys))
            {
                Add(code);
            }
        }

        return keys;
    }

    /// <summary>Parses space-separated chords into press instructions.</summary>
    public static bool TryParseSequence(string? text, out IReadOnlyList<KeyChord> chords, out string? error)
    {
        var parsed = new List<KeyChord>();
        chords = parsed;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Keys must not be empty.";
            return false;
        }

        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseChord(token, out var chord, out error))
            {
                return false;
            }

            parsed.Add(chord!);
        }

        if (parsed.Count > 64)
        {
            error = "At most 64 key chords can be sent in one command.";
            return false;
        }

        return true;
    }

    private static bool TryParseChord(string token, out KeyChord? chord, out string? error)
    {
        chord = null;
        error = null;

        // A literal '+' key is written "+" alone or as the last part of a chord ("ctrl++").
        string[] parts;
        if (token == "+")
        {
            parts = ["plus"];
        }
        else if (token.EndsWith("++", StringComparison.Ordinal))
        {
            parts = [.. token[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries), "plus"];
        }
        else
        {
            parts = token.Split('+', StringSplitOptions.RemoveEmptyEntries);
        }

        if (parts.Length == 0)
        {
            error = $"Key chord '{token}' is empty.";
            return false;
        }

        var modifiers = new List<ushort>();
        var keys = new List<ushort>();
        foreach (var part in parts)
        {
            if (Modifiers.TryGetValue(part, out var modifier))
            {
                if (!modifiers.Contains(modifier))
                {
                    modifiers.Add(modifier);
                }

                continue;
            }

            if (part.Length > 32 || !Keys.TryGetValue(part, out var code))
            {
                error = $"Unknown key '{(part.Length > 32 ? part[..32] + "..." : part)}'.";
                return false;
            }

            // Several regular keys go down together ("w+d" is a diagonal move in a game).
            if (!keys.Contains(code))
            {
                keys.Add(code);
            }

            // "+" is Shift and the "=" key on a US layout; without Shift the key would type "=".
            if (string.Equals(part, "plus", StringComparison.OrdinalIgnoreCase) && !modifiers.Contains(VkShift))
            {
                modifiers.Add(VkShift);
            }
        }

        if (keys.Count > MaxKeysPerChord)
        {
            error = $"Key chord '{token}' presses {keys.Count} keys at once; at most {MaxKeysPerChord} are allowed.";
            return false;
        }

        chord = new KeyChord(modifiers, keys);
        return true;
    }

    private static Dictionary<ushort, string> BuildNames()
    {
        var names = new Dictionary<ushort, string>
        {
            [VkControl] = "ctrl",
            [VkShift] = "shift",
            [VkMenu] = "alt",
            [VkLeftWindows] = "win"
        };

        // The first spelling in the table wins: "enter" over "return", "pageup" over "pgup".
        foreach (var (name, code) in Keys)
        {
            names.TryAdd(code, name);
        }

        return names;
    }

    private static Dictionary<string, ushort> BuildKeys()
    {
        var keys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["backspace"] = 0x08,
            ["tab"] = VkTab,
            ["enter"] = VkReturn,
            ["return"] = VkReturn,
            ["pause"] = 0x13,
            ["capslock"] = 0x14,
            ["esc"] = 0x1B,
            ["escape"] = 0x1B,
            ["space"] = 0x20,
            ["pageup"] = 0x21,
            ["pgup"] = 0x21,
            ["pagedown"] = 0x22,
            ["pgdn"] = 0x22,
            ["end"] = 0x23,
            ["home"] = 0x24,
            ["left"] = 0x25,
            ["up"] = 0x26,
            ["right"] = 0x27,
            ["down"] = 0x28,
            ["arrowleft"] = 0x25,
            ["arrowup"] = 0x26,
            ["arrowright"] = 0x27,
            ["arrowdown"] = 0x28,
            ["printscreen"] = 0x2C,
            ["prtsc"] = 0x2C,
            ["insert"] = 0x2D,
            ["ins"] = 0x2D,
            ["delete"] = 0x2E,
            ["del"] = 0x2E,
            ["menu"] = 0x5D,
            ["apps"] = 0x5D,
            ["numlock"] = 0x90,
            ["scrolllock"] = 0x91,
            ["multiply"] = 0x6A,
            ["add"] = 0x6B,
            ["subtract"] = 0x6D,
            ["decimal"] = 0x6E,
            ["divide"] = 0x6F,
            ["semicolon"] = 0xBA,
            [";"] = 0xBA,
            ["equal"] = 0xBB,
            ["equals"] = 0xBB,
            ["plus"] = 0xBB,
            ["="] = 0xBB,
            ["comma"] = 0xBC,
            [","] = 0xBC,
            ["minus"] = 0xBD,
            ["-"] = 0xBD,
            ["period"] = 0xBE,
            ["."] = 0xBE,
            ["slash"] = 0xBF,
            ["/"] = 0xBF,
            ["backtick"] = 0xC0,
            ["grave"] = 0xC0,
            ["`"] = 0xC0,
            ["bracketleft"] = 0xDB,
            ["["] = 0xDB,
            ["backslash"] = 0xDC,
            ["\\"] = 0xDC,
            ["bracketright"] = 0xDD,
            ["]"] = 0xDD,
            ["quote"] = 0xDE,
            ["apostrophe"] = 0xDE,
            ["'"] = 0xDE
        };

        for (var letter = 'a'; letter <= 'z'; letter++)
        {
            keys[letter.ToString()] = (ushort)char.ToUpperInvariant(letter);
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            keys[digit.ToString()] = (ushort)('0' + digit);
            keys[$"numpad{digit}"] = (ushort)(0x60 + digit);
        }

        for (var function = 1; function <= 24; function++)
        {
            keys[$"f{function}"] = (ushort)(0x70 + function - 1);
        }

        return keys;
    }
}
