using AgentSeat.Core.Agent;

namespace AgentSeat.Core.Tests;

public sealed class AgentKeyMapTests
{
    private static IReadOnlyList<KeyChord> Parse(string text)
    {
        Assert.True(AgentKeyMap.TryParseSequence(text, out var chords, out var error), error);
        return chords;
    }

    [Fact]
    public void ParsesModifiersAndKeyCaseInsensitively()
    {
        var chord = Assert.Single(Parse("Ctrl+SHIFT+t"));

        Assert.Equal(new[] { AgentKeyMap.VkControl, AgentKeyMap.VkShift }, chord.Modifiers);
        Assert.Equal((ushort)'T', chord.Key);
    }

    [Fact]
    public void ParsesSequencesSeparatedBySpaces()
    {
        var chords = Parse("ctrl+a Delete");

        Assert.Equal(2, chords.Count);
        Assert.Equal((ushort)0x2E, chords[1].Key);
        Assert.Empty(chords[1].Modifiers);
    }

    [Theory]
    [InlineData("ENTER", 0x0D)]
    [InlineData("arrowdown", 0x28)]
    [InlineData("pgdn", 0x22)]
    [InlineData("F12", 0x7B)]
    [InlineData("numpad5", 0x65)]
    [InlineData("/", 0xBF)]
    [InlineData("ESC", 0x1B)]
    [InlineData("5", 0x35)]
    public void ResolvesNamedKeys(string name, int expected) =>
        Assert.Equal((ushort)expected, Parse(name)[0].Key);

    [Fact]
    public void ModifierAloneTapsTheModifier()
    {
        var chord = Assert.Single(Parse("win"));

        Assert.Null(chord.Key);
        Assert.Equal(new[] { AgentKeyMap.VkLeftWindows }, chord.Modifiers);
    }

    [Fact]
    public void LiteralPlusKeyIsSupported()
    {
        var plus = Parse("+")[0];
        Assert.Equal((ushort)0xBB, plus.Key);
        Assert.Equal(new[] { AgentKeyMap.VkShift }, plus.Modifiers); // "+" is Shift+"=", not the bare "=" key

        var zoom = Assert.Single(Parse("ctrl++"));
        Assert.Equal(new[] { AgentKeyMap.VkControl, AgentKeyMap.VkShift }, zoom.Modifiers);
        Assert.Equal((ushort)0xBB, zoom.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ctrl+bogus")]
    [InlineData("w+bogus")]
    [InlineData("a+b+c+d+e+f+g+h+i")]
    public void RejectsUnknownKeysAndOversizedChords(string text) =>
        Assert.False(AgentKeyMap.TryParseSequence(text, out _, out var error) || error is null);

    [Fact]
    public void RegularKeysInOneChordGoDownTogether()
    {
        // A diagonal move in a game: W and D held at the same time.
        var diagonal = Assert.Single(Parse("w+d"));
        Assert.Empty(diagonal.Modifiers);
        Assert.Equal(new[] { (ushort)'W', (ushort)'D' }, diagonal.Keys);

        var sprint = Assert.Single(Parse("shift+W+A"));
        Assert.Equal(new[] { AgentKeyMap.VkShift }, sprint.Modifiers);
        Assert.Equal(new[] { (ushort)'W', (ushort)'A' }, sprint.Keys);

        Assert.Equal(new[] { (ushort)'W' }, Assert.Single(Parse("w+w")).Keys);
        Assert.Equal(new[] { (ushort)0x26, (ushort)0x27 }, Assert.Single(Parse("up+right")).Keys);
    }

    [Fact]
    public void KeysInListsEveryKeyOnceModifiersFirstPerChord()
    {
        Assert.Equal(
            new[] { (ushort)'W', (ushort)'D', AgentKeyMap.VkShift },
            AgentKeyMap.KeysIn(Parse("w+d shift d")));
    }

    [Fact]
    public void ArrowsAndNavigationKeysAreExtended()
    {
        Assert.True(AgentKeyMap.IsExtendedKey(0x25));
        Assert.True(AgentKeyMap.IsExtendedKey(0x2E));
        Assert.False(AgentKeyMap.IsExtendedKey(0x41));
    }
}
