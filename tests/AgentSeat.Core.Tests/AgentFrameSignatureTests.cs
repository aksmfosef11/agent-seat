using AgentSeat.Core.Agent;

namespace AgentSeat.Core.Tests;

public sealed class AgentFrameSignatureTests
{
    private sealed class Frame(int width, int height)
    {
        internal int Width { get; } = width;

        internal int Height { get; } = height;

        internal byte[] Pixels { get; } = new byte[width * height * 4];

        internal Frame Set(int x, int y, byte blue, byte alpha = 255)
        {
            var offset = ((y * Width) + x) * 4;
            Pixels[offset] = blue;
            Pixels[offset + 3] = alpha;
            return this;
        }

        internal AgentFrameSignature Sign(int originX = 0, int originY = 0) =>
            AgentFrameSignature.Compute(Pixels, Width * 4, new AgentRegion(originX, originY, Width, Height));

        internal Frame Copy()
        {
            var copy = new Frame(Width, Height);
            Pixels.CopyTo(copy.Pixels, 0);
            return copy;
        }
    }

    [Fact]
    public void IdenticalFramesHaveNoChanges()
    {
        var frame = new Frame(128, 64).Set(5, 5, 9);

        var regions = frame.Copy().Sign().ChangedRegions(frame.Sign(), maxRegions: 4);

        Assert.NotNull(regions);
        Assert.Empty(regions);
        Assert.True(frame.Sign().SameFrameAs(frame.Copy().Sign()));
    }

    [Fact]
    public void OnePixelChangeIsReportedAsItsTile()
    {
        var before = new Frame(128, 96);
        var after = before.Copy().Set(70, 40, 200);

        var regions = after.Sign().ChangedRegions(before.Sign(), maxRegions: 4)!;

        Assert.Equal([new AgentRegion(64, 32, 32, 32)], regions);
    }

    [Fact]
    public void RegionsAreInScreenCoordinatesAndClippedAtTheFrameEdge()
    {
        // 70 px is not a multiple of the tile size: the last column is only 6 px wide.
        var before = new Frame(70, 40);
        var after = before.Copy().Set(69, 39, 1);

        var regions = after.Sign(originX: -100, originY: 10).ChangedRegions(before.Sign(originX: -100, originY: 10), maxRegions: 4)!;

        Assert.Equal([new AgentRegion(-100 + 64, 10 + 32, 6, 8)], regions);
    }

    [Fact]
    public void ChangesOneTileApartAreGroupedAndFarOnesAreNot()
    {
        var before = new Frame(320, 64);
        var after = before.Copy()
            .Set(1, 1, 1) // tile 0
            .Set(65, 1, 1) // tile 2: one unchanged tile between, same group
            .Set(300, 1, 1); // tile 9: far away, its own group

        var regions = after.Sign().ChangedRegions(before.Sign(), maxRegions: 4)!;

        Assert.Equal([new AgentRegion(0, 0, 96, 32), new AgentRegion(288, 0, 32, 32)], regions);
    }

    [Fact]
    public void TooManyGroupsCollapseIntoOneBoundingBox()
    {
        var before = new Frame(640, 32);
        var after = before.Copy();
        for (var x = 0; x < 640; x += 128)
        {
            _ = after.Set(x, 0, 1);
        }

        var regions = after.Sign().ChangedRegions(before.Sign(), maxRegions: 2)!;

        Assert.Equal([new AgentRegion(0, 0, 544, 32)], regions);
    }

    [Fact]
    public void AlphaIsIgnored()
    {
        var before = new Frame(64, 64).Set(3, 3, 7, alpha: 255);
        var after = new Frame(64, 64).Set(3, 3, 7, alpha: 0);

        Assert.Empty(after.Sign().ChangedRegions(before.Sign(), maxRegions: 4)!);
    }

    [Fact]
    public void FramesOfDifferentScreensCannotBeCompared()
    {
        var small = new Frame(64, 64).Sign();
        var moved = new Frame(64, 64).Sign(originX: 1);
        var larger = new Frame(96, 64).Sign();

        Assert.Null(small.ChangedRegions(moved, maxRegions: 4));
        Assert.Null(small.ChangedRegions(larger, maxRegions: 4));
    }

    [Fact]
    public void EncodingRoundTrips()
    {
        var signature = new Frame(1280, 800).Set(640, 400, 3).Sign(originX: -1280);

        Assert.True(AgentFrameSignature.TryDecode(signature.Encode(), out var decoded));

        Assert.Equal(signature.Area, decoded.Area);
        Assert.True(decoded.SameFrameAs(signature));
        Assert.StartsWith("t32:-1280,0,1280,800:", signature.Encode(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x32:0,0,32,32:AAAAAA==")]
    [InlineData("t32:0,0,32,32")]
    [InlineData("t32:0,0,32:AAAAAA==")]
    [InlineData("t32:0,0,0,32:")]
    [InlineData("t32:0,0,99999,32:AAAAAA==")]
    [InlineData("t32:0,0,32,32:!!!!")]
    [InlineData("t32:0,0,64,32:AAAAAA==")] // two tiles need 8 bytes, this is 4
    [InlineData("t32:0,0,32,32:AAAAAAAAAAA=")] // one tile needs 4 bytes, this is 8
    public void MalformedSignaturesAreRefused(string? text)
    {
        Assert.False(AgentFrameSignature.TryDecode(text, out _));
    }

    [Fact]
    public void OverlongSignaturesAreRefusedWithoutParsing()
    {
        Assert.False(AgentFrameSignature.TryDecode("t32:" + new string('A', AgentFrameSignature.MaxEncodedLength), out _));
    }

    [Fact]
    public void PaddingStaysOnTheFrame()
    {
        var signature = new Frame(100, 100).Sign(originX: 10, originY: 10);

        Assert.Equal(new AgentRegion(10, 10, 48, 48), signature.Pad(new AgentRegion(10, 10, 32, 32), 16));
        Assert.Equal(new AgentRegion(26, 26, 64, 64), signature.Pad(new AgentRegion(42, 42, 32, 32), 16));
    }

    [Fact]
    public void CropsAreOnlyWorthItForASmallShareOfTheScreen()
    {
        var signature = new Frame(100, 100).Sign();

        Assert.True(signature.WorthCropping([new AgentRegion(0, 0, 40, 40), new AgentRegion(50, 50, 40, 40)]));
        Assert.False(signature.WorthCropping([new AgentRegion(0, 0, 80, 80)]));
    }
}
