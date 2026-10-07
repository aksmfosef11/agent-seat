using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentSeat.Core.Agent;

namespace AgentSeat.AgentHelper;

/// <summary>
/// Injects mouse and keyboard input into this process's input desktop, which for a seat is that
/// seat's own WTS session. The host console never sees any of it.
/// </summary>
internal static class InputInjector
{
    private const int TypeChunkCharacters = 64;
    private const int MaxHeldKeys = 16;
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.Input>();

    // What key_down / mouse_down left held across commands, in press order.
    private static readonly List<ushort> HeldKeys = [];
    private static readonly List<string> HeldButtons = [];
    private static long _lastInputTicks = Environment.TickCount64;

    /// <summary>Every command and the idle-release watchdog run under this lock, so input never interleaves.</summary>
    internal static object Sync { get; } = new();

    /// <summary>Names of what is held right now, for example <c>shift</c>, <c>w</c>, <c>mouse_left</c>.</summary>
    internal static IReadOnlyList<string> HeldNames() =>
        [.. HeldKeys.Select(AgentKeyMap.NameOf), .. HeldButtons.Select(button => $"mouse_{button}")];

    /// <summary>Records that the agent did something, which keeps held input alive.</summary>
    internal static void MarkActivity() => Volatile.Write(ref _lastInputTicks, Environment.TickCount64);

    internal static (int X, int Y) CursorPosition()
    {
        _ = NativeMethods.GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    internal static void Move(int x, int y)
    {
        if (!ScreenCapture.GetDesktop().Contains(x, y))
        {
            var desktop = ScreenCapture.GetDesktop();
            throw new AgentActionException(
                "out_of_bounds",
                $"({x},{y}) is outside the {desktop.Width}x{desktop.Height} screen at ({desktop.X},{desktop.Y}).");
        }

        if (!NativeMethods.SetCursorPos(x, y))
        {
            throw DesktopUnavailable();
        }

        Thread.Sleep(15);
    }

    internal static void Click(int? x, int? y, string? button, int clicks, string[]? modifiers)
    {
        var (down, up) = ButtonFlags(NotHeld(button));
        WithModifiers(modifiers, () =>
        {
            if (x is { } px && y is { } py)
            {
                Move(px, py);
            }

            for (var index = 0; index < clicks; index++)
            {
                SendMouse(down);
                try
                {
                    Thread.Sleep(25);
                    SendMouse(up);
                }
                catch
                {
                    SendQuiet(MouseInput(up, 0)); // Never leave the button held down.
                    throw;
                }

                if (index < clicks - 1)
                {
                    Thread.Sleep(45);
                }
            }
        });
    }

    internal static void Drag(IReadOnlyList<AgentPoint> path, string? button, string[]? modifiers)
    {
        var (down, up) = ButtonFlags(NotHeld(button));
        WithModifiers(modifiers, () =>
        {
            Move(path[0].X, path[0].Y);
            SendMouse(down);
            try
            {
                Thread.Sleep(40);
                for (var index = 1; index < path.Count; index++)
                {
                    GlideTo(path[index - 1], path[index]);
                }

                Thread.Sleep(40);
            }
            finally
            {
                SendQuiet(MouseInput(up, 0));
            }
        });
    }

    internal static void Scroll(int? x, int? y, string direction, int amount, string[]? modifiers)
    {
        var horizontal = direction is "left" or "right";
        var positive = direction is "up" or "right";
        var delta = unchecked((uint)(positive ? 120 : -120));
        WithModifiers(modifiers, () =>
        {
            if (x is { } px && y is { } py)
            {
                Move(px, py);
            }

            for (var notch = 0; notch < amount; notch++)
            {
                Send(MouseInput(horizontal ? NativeMethods.MouseEventHorizontalWheel : NativeMethods.MouseEventWheel, delta));
                Thread.Sleep(12);
            }
        });
    }

    /// <summary>Types text as Unicode key events so any layout and any script works; newline is Enter.</summary>
    internal static void TypeText(string text)
    {
        // A typed line break or tab presses and releases Enter / Tab, which would let go of one key_down is holding.
        if (((text.Contains('\n') || text.Contains('\r')) && HeldKeys.Contains(AgentKeyMap.VkReturn)) ||
            (text.Contains('\t') && HeldKeys.Contains(AgentKeyMap.VkTab)))
        {
            throw new AgentActionException(
                "invalid_request",
                "Enter or Tab is being held (key_down). Release it with key_up before typing line breaks or tabs.");
        }

        var batch = new List<NativeMethods.Input>(TypeChunkCharacters * 2);
        var typed = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (character)
            {
                case '\r' when index + 1 < text.Length && text[index + 1] == '\n':
                    continue;
                case '\r' or '\n':
                    AddKey(batch, AgentKeyMap.VkReturn, keyUp: false);
                    AddKey(batch, AgentKeyMap.VkReturn, keyUp: true);
                    break;
                case '\t':
                    AddKey(batch, AgentKeyMap.VkTab, keyUp: false);
                    AddKey(batch, AgentKeyMap.VkTab, keyUp: true);
                    break;
                default:
                    batch.Add(UnicodeKey(character, keyUp: false));
                    batch.Add(UnicodeKey(character, keyUp: true));
                    break;
            }

            if (++typed % TypeChunkCharacters == 0)
            {
                Send([.. batch]);
                batch.Clear();
                Thread.Sleep(10);
            }
        }

        if (batch.Count > 0)
        {
            Send([.. batch]);
        }
    }

    internal static void PressKeys(string keys, int repeat = 1)
    {
        var chords = ParseChords(keys);
        if (chords.SelectMany(chord => chord.Keys).Where(HeldKeys.Contains).Select(key => (ushort?)key).FirstOrDefault() is { } held)
        {
            throw new AgentActionException(
                "invalid_request",
                $"'{AgentKeyMap.NameOf(held)}' is being held (key_down). Release it with key_up before pressing it again.");
        }

        for (var round = 0; round < repeat; round++)
        {
            for (var index = 0; index < chords.Count; index++)
            {
                if (round > 0 || index > 0)
                {
                    Thread.Sleep(30);
                }

                PressChord(chords[index]);
            }
        }
    }

    /// <summary>Presses keys and leaves them down after the command returns. Keys already held stay as they are.</summary>
    internal static void KeyDown(string keys)
    {
        var codes = AgentKeyMap.KeysIn(ParseChords(keys));
        if (HeldKeys.Union(codes).Count() > MaxHeldKeys)
        {
            throw new AgentActionException("invalid_request", $"At most {MaxHeldKeys} keys can be held at once.");
        }

        foreach (var code in codes)
        {
            if (HeldKeys.Contains(code))
            {
                continue;
            }

            Send(KeyboardInput(code, keyUp: false));
            HeldKeys.Add(code);
            Thread.Sleep(15);
        }
    }

    /// <summary>Lets go of the named held keys (latest first), or of every held key when none are named.</summary>
    internal static void KeyUp(string? keys)
    {
        var codes = keys is null
            ? [.. HeldKeys]
            : AgentKeyMap.KeysIn(ParseChords(keys)).Where(HeldKeys.Contains).ToList();
        for (var index = codes.Count - 1; index >= 0; index--)
        {
            Send(KeyboardInput(codes[index], keyUp: true));
            _ = HeldKeys.Remove(codes[index]);
        }
    }

    /// <summary>Presses a mouse button (after moving to x, y when given) and leaves it down; moves then drag.</summary>
    internal static void MouseDown(int? x, int? y, string? button)
    {
        var name = ButtonName(button);
        if (x is { } px && y is { } py)
        {
            Move(px, py);
        }

        if (HeldButtons.Contains(name))
        {
            return;
        }

        Send(MouseInput(ButtonFlags(name).Down, 0));
        HeldButtons.Add(name);
    }

    /// <summary>Moves to x, y when given (completing a drag), then lets go of a held button.</summary>
    internal static void MouseUp(int? x, int? y, string? button)
    {
        var name = ButtonName(button);
        if (x is { } px && y is { } py)
        {
            Move(px, py);
        }

        if (HeldButtons.Contains(name))
        {
            Send(MouseInput(ButtonFlags(name).Up, 0));
            _ = HeldButtons.Remove(name);
        }
    }

    /// <summary>
    /// Holds keys and/or a mouse button for a fixed time and always lets go afterwards, even when Windows rejects
    /// input halfway. Keys that key_down already holds are left held.
    /// </summary>
    internal static void Hold(string? keys, string? button, int? x, int? y, int milliseconds)
    {
        var codes = keys is null ? [] : AgentKeyMap.KeysIn(ParseChords(keys));
        var buttonName = button is null ? null : NotHeld(button);
        var pressed = new List<ushort>();
        var buttonDown = false;
        try
        {
            if (x is { } px && y is { } py)
            {
                Move(px, py);
            }

            foreach (var code in codes.Where(code => !HeldKeys.Contains(code)))
            {
                Send(KeyboardInput(code, keyUp: false));
                pressed.Add(code);
            }

            if (buttonName is not null)
            {
                Send(MouseInput(ButtonFlags(buttonName).Down, 0));
                buttonDown = true;
            }

            Thread.Sleep(milliseconds);
        }
        finally
        {
            if (buttonDown)
            {
                SendQuiet(MouseInput(ButtonFlags(buttonName).Up, 0));
            }

            for (var index = pressed.Count - 1; index >= 0; index--)
            {
                SendQuiet(KeyboardInput(pressed[index], keyUp: true));
            }
        }
    }

    /// <summary>
    /// Lets go of everything key_down and mouse_down left held and returns what was released. Anything Windows
    /// refuses to release (locked screen, secure prompt) stays tracked, so it is still reported as held and the
    /// watchdog tries again.
    /// </summary>
    internal static IReadOnlyList<string> ReleaseHeld()
    {
        var released = new List<string>();
        for (var index = HeldButtons.Count - 1; index >= 0; index--)
        {
            if (SendQuiet(MouseInput(ButtonFlags(HeldButtons[index]).Up, 0)))
            {
                released.Add($"mouse_{HeldButtons[index]}");
                HeldButtons.RemoveAt(index);
            }
        }

        for (var index = HeldKeys.Count - 1; index >= 0; index--)
        {
            if (SendQuiet(KeyboardInput(HeldKeys[index], keyUp: true)))
            {
                released.Add(AgentKeyMap.NameOf(HeldKeys[index]));
                HeldKeys.RemoveAt(index);
            }
        }

        return released;
    }

    /// <summary>
    /// The watchdog's dead-man switch: an agent that crashed or forgot must not leave a key or button down for
    /// good. Releases held input when nothing has been done for <paramref name="limit"/>; returns what it released.
    /// </summary>
    internal static IReadOnlyList<string> ReleaseIfIdle(TimeSpan limit)
    {
        lock (Sync)
        {
            if ((HeldKeys.Count == 0 && HeldButtons.Count == 0) ||
                Environment.TickCount64 - Volatile.Read(ref _lastInputTicks) < limit.TotalMilliseconds)
            {
                return [];
            }

            return ReleaseHeld();
        }
    }

    private static IReadOnlyList<KeyChord> ParseChords(string keys) =>
        AgentKeyMap.TryParseSequence(keys, out var chords, out var error)
            ? chords
            : throw new AgentActionException("invalid_request", error ?? "Invalid keys.");

    private static string ButtonName(string? button)
    {
        var name = (button ?? "left").ToLowerInvariant();
        _ = ButtonFlags(name); // rejects unknown buttons
        return name;
    }

    /// <summary>A click or drag would let go of a button that mouse_down is holding, so it is refused instead.</summary>
    private static string NotHeld(string? button)
    {
        var name = ButtonName(button);
        return HeldButtons.Contains(name)
            ? throw new AgentActionException(
                "invalid_request",
                $"The {name} mouse button is being held (mouse_down). Release it with mouse_up first.")
            : name;
    }

    private static void PressChord(KeyChord chord)
    {
        var pressed = new List<ushort>();
        var down = new List<ushort>();
        try
        {
            foreach (var modifier in chord.Modifiers)
            {
                if (HeldKeys.Contains(modifier))
                {
                    continue; // key_down holds it: it is already down and has to stay down afterwards
                }

                Send(KeyboardInput(modifier, keyUp: false));
                pressed.Add(modifier);
            }

            Thread.Sleep(chord.Keys.Count > 0 ? 15 : 20);
            if (chord.Keys.Count > 0)
            {
                // Every key of the chord goes down before any comes back up, so they register as pressed together.
                foreach (var key in chord.Keys)
                {
                    Send(KeyboardInput(key, keyUp: false));
                    down.Add(key);
                }

                Thread.Sleep(20);
                for (var index = down.Count - 1; index >= 0; index--)
                {
                    Send(KeyboardInput(down[index], keyUp: true));
                    down.RemoveAt(index);
                }
            }
        }
        finally
        {
            // Never leave a key held down, whatever failed.
            for (var index = down.Count - 1; index >= 0; index--)
            {
                SendQuiet(KeyboardInput(down[index], keyUp: true));
            }

            for (var index = pressed.Count - 1; index >= 0; index--)
            {
                SendQuiet(KeyboardInput(pressed[index], keyUp: true));
            }
        }
    }

    private static void WithModifiers(string[]? modifiers, Action action)
    {
        var codes = (modifiers ?? [])
            .Select(name => AgentKeyMap.ResolveModifier(name) ?? throw new AgentActionException(
                "invalid_request",
                $"Unknown modifier '{name}'."))
            .Distinct()
            .Where(code => !HeldKeys.Contains(code)) // held by key_down: leave it down
            .ToArray();
        var pressed = new List<ushort>();
        try
        {
            foreach (var code in codes)
            {
                Send(KeyboardInput(code, keyUp: false));
                pressed.Add(code);
            }

            action();
        }
        finally
        {
            for (var index = pressed.Count - 1; index >= 0; index--)
            {
                SendQuiet(KeyboardInput(pressed[index], keyUp: true));
            }
        }
    }

    private static void GlideTo(AgentPoint from, AgentPoint to)
    {
        var distance = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        var steps = Math.Clamp((int)(distance / 60), 1, 8);
        for (var step = 1; step <= steps; step++)
        {
            var progress = step / (double)steps;
            Move(
                (int)Math.Round(from.X + (to.X - from.X) * progress),
                (int)Math.Round(from.Y + (to.Y - from.Y) * progress));
        }
    }

    private static (uint Down, uint Up) ButtonFlags(string? button) => button?.ToLowerInvariant() switch
    {
        null or "left" => (NativeMethods.MouseEventLeftDown, NativeMethods.MouseEventLeftUp),
        "right" => (NativeMethods.MouseEventRightDown, NativeMethods.MouseEventRightUp),
        "middle" => (NativeMethods.MouseEventMiddleDown, NativeMethods.MouseEventMiddleUp),
        _ => throw new AgentActionException("invalid_request", $"Unknown mouse button '{button}'.")
    };

    private static void SendMouse(uint flags) => Send(MouseInput(flags, 0));

    /// <summary>Releases for held keys and buttons: each one is attempted on its own and never throws.</summary>
    private static bool SendQuiet(NativeMethods.Input input) => NativeMethods.SendInput(1, [input], InputSize) == 1;

    /// <summary>
    /// Run at helper start, and by the kill switch after it killed the helper. A helper that was killed mid-click,
    /// mid-chord or while key_down / mouse_down held something leaves keys or a mouse button logically held in the
    /// session, so every later input would be modified (or a game character keeps walking). Releasing modifiers and
    /// buttons that are not down is harmless; any other key is released only when the session reports it down.
    /// </summary>
    internal static void ReleaseEverything()
    {
        foreach (ushort key in new ushort[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
        {
            SendQuiet(KeyboardInput(key, keyUp: true));
        }

        // 0x01-0x07 are mouse buttons and cancel; they are handled below or never held.
        for (ushort key = 0x08; key <= 0xFE; key++)
        {
            if ((NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0)
            {
                SendQuiet(KeyboardInput(key, keyUp: true));
            }
        }

        foreach (var up in new[]
                 {
                     NativeMethods.MouseEventLeftUp,
                     NativeMethods.MouseEventRightUp,
                     NativeMethods.MouseEventMiddleUp
                 })
        {
            SendQuiet(MouseInput(up, 0));
        }
    }

    private static NativeMethods.Input MouseInput(uint flags, uint data) => new()
    {
        Type = NativeMethods.InputMouse,
        Data = new NativeMethods.InputUnion
        {
            Mouse = new NativeMethods.MouseInput { Flags = flags, MouseData = data }
        }
    };

    private static NativeMethods.Input KeyboardInput(ushort virtualKey, bool keyUp)
    {
        var flags = keyUp ? NativeMethods.KeyEventKeyUp : 0u;
        if (AgentKeyMap.IsExtendedKey(virtualKey))
        {
            flags |= NativeMethods.KeyEventExtendedKey;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = (ushort)NativeMethods.MapVirtualKeyW(virtualKey, NativeMethods.MapVirtualKeyToScanCode),
                    Flags = flags
                }
            }
        };
    }

    private static NativeMethods.Input UnicodeKey(char character, bool keyUp) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                ScanCode = character,
                Flags = NativeMethods.KeyEventUnicode | (keyUp ? NativeMethods.KeyEventKeyUp : 0u)
            }
        }
    };

    private static void AddKey(List<NativeMethods.Input> batch, ushort virtualKey, bool keyUp) =>
        batch.Add(KeyboardInput(virtualKey, keyUp));

    private static void Send(params NativeMethods.Input[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length)
        {
            throw DesktopUnavailable();
        }
    }

    private static AgentActionException DesktopUnavailable()
    {
        var error = Marshal.GetLastWin32Error();
        return new AgentActionException(
            "desktop_unavailable",
            error == NativeMethods.ErrorAccessDenied
                ? "The session cannot accept input right now (locked screen, secure prompt, or a window running with higher privileges)."
                : "Windows rejected the input.",
            new Win32Exception(error));
    }
}
