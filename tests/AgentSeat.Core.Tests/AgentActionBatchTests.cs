using System.Text.Json.Nodes;
using AgentSeat.Core.Agent;

namespace AgentSeat.Core.Tests;

public sealed class AgentActionBatchTests
{
    private static AgentBatchRequest Parse(string json)
    {
        Assert.True(
            AgentActionBatch.TryParse(JsonNode.Parse(json), out var batch, out var error),
            error);
        return batch!;
    }

    private static string ParseError(string json)
    {
        Assert.False(AgentActionBatch.TryParse(JsonNode.Parse(json), out _, out var error));
        return error!;
    }

    [Fact]
    public void ParsesOpenAiComputerCallActionsVerbatim()
    {
        // Shape taken from the OpenAI/Azure computer-use computer_call.actions array.
        var batch = Parse("""
            {
              "type": "computer_call",
              "actions": [
                { "type": "click", "button": "left", "x": 135, "y": 193 },
                { "type": "double_click", "x": 10, "y": 20 },
                { "type": "type", "text": "hello" },
                { "type": "keypress", "keys": ["CTRL", "A"] },
                { "type": "move", "x": 5, "y": 6 },
                { "type": "wait" },
                { "type": "screenshot" }
              ]
            }
            """);

        Assert.Equal(
            [AgentActions.Click, AgentActions.Click, AgentActions.Type, AgentActions.Key, AgentActions.MouseMove, AgentActions.Wait],
            batch.Steps.Select(step => step.Request.Action));
        Assert.Equal(135, batch.Steps[0].Request.X);
        Assert.Equal("left", batch.Steps[0].Request.Button);
        Assert.Equal(2, batch.Steps[1].Request.Clicks);
        Assert.Equal("CTRL+A", batch.Steps[3].Request.Keys);
        Assert.Equal(AgentActionBatch.DefaultWaitMilliseconds, batch.Steps[5].Request.Milliseconds);
        Assert.True(batch.Screenshot);
        Assert.Equal(AgentActionBatch.DefaultSettleMilliseconds, batch.SettleMilliseconds);
    }

    [Fact]
    public void AcceptsBareArraySingleActionAndRoundsFloatCoordinates()
    {
        Assert.Single(Parse("""[{ "type": "click", "x": 1.4, "y": 2.6 }]""").Steps);

        var single = Parse("""{ "type": "click", "x": 10.5, "y": 20.4 }""");
        Assert.Equal(11, single.Steps[0].Request.X);
        Assert.Equal(20, single.Steps[0].Request.Y);
    }

    [Fact]
    public void ScrollPixelOffsetsBecomeWheelNotches()
    {
        var batch = Parse("""
            [{ "type": "scroll", "x": 100, "y": 100, "scroll_x": -250, "scroll_y": 300 }]
            """);

        Assert.Equal(2, batch.Steps.Count);
        Assert.All(batch.Steps, step => Assert.Equal(0, step.SourceIndex));
        Assert.Equal("down", batch.Steps[0].Request.Direction);
        Assert.Equal(3, batch.Steps[0].Request.Amount);
        Assert.Equal("left", batch.Steps[1].Request.Direction);
        Assert.Equal(3, batch.Steps[1].Request.Amount); // 250px rounds up to 3 notches
    }

    [Fact]
    public void DragPathAndBackButtonAreTranslated()
    {
        var batch = Parse("""
            [
              { "type": "drag", "path": [ { "x": 1, "y": 2 }, { "x": 30, "y": 40 }, { "x": 50, "y": 60 } ] },
              { "type": "click", "button": "back", "x": 0, "y": 0 },
              { "type": "click", "button": "wheel", "x": 7, "y": 8 }
            ]
            """);

        Assert.Equal(3, batch.Steps[0].Request.Points!.Length);
        Assert.Equal(new AgentPoint(50, 60), batch.Steps[0].Request.Points![^1]);
        Assert.Equal("alt+left", batch.Steps[1].Request.Keys);
        Assert.Equal("middle", batch.Steps[2].Request.Button);
    }

    [Fact]
    public void KeypressSpaceAndPlusKeysKeepTheirMeaning()
    {
        var batch = Parse("""
            [
              { "type": "keypress", "keys": [" "] },
              { "type": "keypress", "keys": ["CTRL", "+"] },
              { "type": "key", "keys": "ctrl+s Return" }
            ]
            """);

        Assert.Equal("space", batch.Steps[0].Request.Keys);
        Assert.Equal("CTRL++", batch.Steps[1].Request.Keys);
        Assert.Equal("ctrl+s Return", batch.Steps[2].Request.Keys);
    }

    [Fact]
    public void NativeActionsPassThroughAndOptionsApply()
    {
        var batch = Parse("""
            {
              "actions": [
                { "type": "launch", "path": "notepad.exe", "arguments": ["a.txt"] },
                { "type": "windows" }
              ],
              "screenshot": false,
              "settleMs": 0,
              "scale": 0.5,
              "format": "jpeg"
            }
            """);

        Assert.Equal("notepad.exe", batch.Steps[0].Request.Path);
        Assert.Equal(new[] { "a.txt" }, batch.Steps[0].Request.Arguments);
        Assert.False(batch.Screenshot);
        Assert.Equal(0, batch.SettleMilliseconds);
        Assert.Equal(0.5, batch.ScreenshotOptions.Scale);
        Assert.Equal("jpeg", batch.ScreenshotOptions.Format);
    }

    [Fact]
    public void EmptyBatchJustTakesAScreenshot()
    {
        var batch = Parse("""{ "actions": [] }""");

        Assert.Empty(batch.Steps);
        Assert.True(batch.Screenshot);
    }

    [Theory]
    [InlineData("""[{ "type": "teleport" }]""", "Unknown action type")]
    [InlineData("""[{ "x": 1 }]""", "Missing 'type'")]
    [InlineData("""[{ "type": "click", "x": 1 }]""", "both x and y")]
    [InlineData("""[{ "type": "click", "button": "laser", "x": 1, "y": 1 }]""", "Unknown mouse button")]
    [InlineData("""[{ "type": "scroll", "x": 1, "y": 1 }]""", "scroll_x/scroll_y")]
    [InlineData("""[{ "type": "keypress", "keys": ["CTRL", "NOPE"] }]""", "Unknown key")]
    [InlineData("""[{ "type": "drag", "path": [ { "x": 1, "y": 1 } ] }]""", "between 2 and")]
    [InlineData("""[{ "type": "type", "text": "" }]""", "must not be empty")]
    [InlineData("""[{ "type": "wait", "ms": 99999 }]""", "Milliseconds")]
    [InlineData("""{ "foo": 1 }""", "Expected an action array")]
    [InlineData("""{ "actions": [], "settleMs": 99999 }""", "settleMs")]
    public void RejectsMalformedActionsWithTheOffendingIndex(string json, string expected)
    {
        var error = ParseError(json);

        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorNamesTheActionIndex()
    {
        var error = ParseError("""[ { "type": "click", "x": 1, "y": 1 }, { "type": "teleport" } ]""");

        Assert.StartsWith("Action 1:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtremeScrollOffsetsAreClampedInsteadOfCrashingTheParser()
    {
        // Math.Abs(int.MinValue) used to throw an unhandled OverflowException.
        var batch = Parse("""[{ "type": "scroll", "x": 1, "y": 1, "scroll_y": -2147483648, "scroll_x": 2147483647 }]""");

        Assert.Equal(2, batch.Steps.Count);
        Assert.All(batch.Steps, step => Assert.Equal(AgentRequestValidator.MaxScrollAmount, step.Request.Amount));
        Assert.Equal("up", batch.Steps[0].Request.Direction);
        Assert.Equal("right", batch.Steps[1].Request.Direction);
    }

    [Fact]
    public void OverlongKeyStringsAreRejected()
    {
        var error = ParseError($$"""[{ "type": "key", "keys": "{{new string('a', AgentRequestValidator.MaxKeysLength + 1)}}" }]""");

        Assert.Contains("limited to", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HeldInputActionsAreTranslated()
    {
        var batch = Parse("""
            [
              { "type": "key_down", "keys": "shift" },
              { "type": "key_up" },
              { "type": "mouse_down", "x": 10, "y": 20, "button": "right" },
              { "type": "mouse_up" },
              { "type": "hold", "keys": "w", "ms": 1500 },
              { "type": "long_press", "x": 5, "y": 6, "ms": 800 },
              { "type": "release_all" }
            ]
            """);

        var steps = batch.Steps.Select(step => step.Request).ToArray();
        Assert.Equal(
            [AgentActions.KeyDown, AgentActions.KeyUp, AgentActions.MouseDown, AgentActions.MouseUp, AgentActions.Hold, AgentActions.Hold, AgentActions.Release],
            steps.Select(request => request.Action));
        Assert.Equal("shift", steps[0].Keys);
        Assert.Null(steps[1].Keys);
        Assert.Equal(("right", 10, 20), (steps[2].Button, steps[2].X, steps[2].Y));
        Assert.Null(steps[3].X);
        Assert.Equal(("w", 1500), (steps[4].Keys, steps[4].Milliseconds));
        Assert.Equal(("left", (string?)null, 800), (steps[5].Button, steps[5].Keys, steps[5].Milliseconds));
    }

    [Fact]
    public void AnthropicHoldAndMouseButtonSpellingsAreAccepted()
    {
        var batch = Parse("""
            [
              { "type": "hold_key", "text": "ctrl+shift", "duration": 1.5 },
              { "type": "left_mouse_down" },
              { "type": "left_mouse_up", "button": "right" }
            ]
            """);

        Assert.Equal(("ctrl+shift", 1500), (batch.Steps[0].Request.Keys, batch.Steps[0].Request.Milliseconds));
        Assert.Equal((AgentActions.MouseDown, "left"), (batch.Steps[1].Request.Action, batch.Steps[1].Request.Button));
        Assert.Equal((AgentActions.MouseUp, "left"), (batch.Steps[2].Request.Action, batch.Steps[2].Request.Button));
    }

    [Fact]
    public void SeveralRegularKeysCanBeHeldOrPressedTogether()
    {
        // OpenAI sends simultaneous keys as one keys[] list; Anthropic and the CLI write "w+d".
        var batch = Parse("""
            [
              { "type": "keypress", "keys": ["W", "D"] },
              { "type": "hold", "keys": "w+d", "ms": 800 },
              { "type": "key_down", "keys": "w+d" },
              { "type": "hold_key", "text": "up+right", "duration": 0.5 }
            ]
            """);

        Assert.Equal("W+D", batch.Steps[0].Request.Keys);
        Assert.Equal("w+d", batch.Steps[1].Request.Keys);
        Assert.Equal(AgentActions.KeyDown, batch.Steps[2].Request.Action);
        Assert.Equal(("up+right", 500), (batch.Steps[3].Request.Keys, batch.Steps[3].Request.Milliseconds));
    }

    [Fact]
    public void KeyRepeatIsCarried()
    {
        var batch = Parse("""[{ "type": "key", "keys": "Down", "repeat": 10 }]""");

        Assert.Equal(10, batch.Steps[0].Request.Repeat);
    }

    [Theory]
    [InlineData("""[{ "type": "hold", "keys": "w", "ms": 10001 }]""", "between 1 and")]
    [InlineData("""[{ "type": "hold", "keys": "w" }]""", "between 1 and")]
    [InlineData("""[{ "type": "hold", "ms": 100 }]""", "keys, a mouse button")]
    [InlineData("""[{ "type": "hold", "keys": "nope", "ms": 100 }]""", "Unknown key")]
    [InlineData("""[{ "type": "key_down" }]""", "must not be empty")]
    [InlineData("""[{ "type": "mouse_down", "x": 1 }]""", "both x and y")]
    [InlineData("""[{ "type": "mouse_up", "button": "laser" }]""", "Button must be")]
    [InlineData("""[{ "type": "key", "keys": "a", "repeat": 0 }]""", "Repeat must be")]
    [InlineData("""[{ "type": "key", "keys": "a b c", "repeat": 100 }]""", "at most 200 chords")]
    [InlineData("""[{ "type": "zoom" }]""", "Zoom needs region")]
    [InlineData("""[{ "type": "zoom", "region": [10, 10, 5, 50] }]""", "x1 > x0")]
    [InlineData("""{ "actions": [], "stableMs": 6000 }""", "stableMs")]
    public void RejectsMalformedHeldInputZoomAndRepeat(string json, string expected)
    {
        Assert.Contains(expected, ParseError(json), StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomTakesARegionScreenshotAndNeverAChangeReport()
    {
        var batch = Parse("""
            { "actions": [ { "type": "zoom", "region": [100, 50, 400, 250] } ], "screenshot": false, "diff": true, "since": "t32:x" }
            """);

        Assert.Empty(batch.Steps);
        Assert.True(batch.Screenshot);
        Assert.Equal(new AgentRegion(100, 50, 300, 200), batch.ScreenshotOptions.Region);
        Assert.Null(batch.ScreenshotOptions.Diff);
        Assert.Null(batch.ScreenshotOptions.Since);
    }

    [Fact]
    public void ZoomAcceptsASizedRegionToo()
    {
        var batch = Parse("""[{ "type": "zoom", "x": 1, "y": 2, "width": 30, "height": 40 }]""");

        Assert.Equal(new AgentRegion(1, 2, 30, 40), batch.ScreenshotOptions.Region);
    }

    [Fact]
    public void ChangeReportAndStableWaitOptionsReachTheScreenshot()
    {
        var batch = Parse("""{ "actions": [], "diff": true, "since": "t32:0,0,32,32:AAAAAA==", "stableMs": 1500 }""");

        Assert.True(batch.ScreenshotOptions.Diff);
        Assert.Equal("t32:0,0,32,32:AAAAAA==", batch.ScreenshotOptions.Since);
        Assert.Equal(1500, batch.ScreenshotOptions.StableMilliseconds);
    }

    [Fact]
    public void RejectsOversizedBatches()
    {
        var actions = string.Join(',', Enumerable.Repeat("""{ "type": "move", "x": 1, "y": 1 }""", 51));

        Assert.Contains("At most", ParseError($"[{actions}]"), StringComparison.Ordinal);
    }
}
