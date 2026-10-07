using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AgentSeat.Core.Agent;

/// <summary>
/// A fingerprint of a screen frame: one 32-bit hash per 32×32 tile, encoded as <c>t32:x,y,width,height:base64</c>.
/// The CLI keeps the fingerprint of the last screenshot it handed the agent and sends it back with the next request,
/// so the helper can report what changed without anyone storing frames: the owner's live preview, a second caller
/// or a helper restart cannot shift the baseline.
/// </summary>
public sealed class AgentFrameSignature
{
    public const int TileSize = 32;

    /// <summary>Enough for an 8K desktop (32,400 tiles).</summary>
    public const int MaxEncodedLength = 200_000;

    /// <summary>Crops are only worth sending while together they cover at most this share of the screen.</summary>
    public const double MaxCropShare = 0.4;

    private const string Prefix = "t32:";
    private const int MaxSide = 16384;
    private readonly uint[] _tiles;

    public AgentFrameSignature(AgentRegion area, uint[] tiles)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(tiles);
        if (area.Width is < 1 or > MaxSide || area.Height is < 1 or > MaxSide)
        {
            throw new ArgumentException("The frame size is out of range.", nameof(area));
        }

        Area = area;
        Columns = TilesAcross(area.Width);
        Rows = TilesAcross(area.Height);
        if (tiles.Length != Columns * Rows)
        {
            throw new ArgumentException($"Expected {Columns * Rows} tile hashes, got {tiles.Length}.", nameof(tiles));
        }

        _tiles = tiles;
    }

    /// <summary>The screen rectangle the frame covers, in real screen pixels.</summary>
    public AgentRegion Area { get; }

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>Hashes a top-down 32-bit BGRA frame of <paramref name="area"/>'s size; the alpha byte is ignored.</summary>
    public static AgentFrameSignature Compute(ReadOnlySpan<byte> pixels, int stride, AgentRegion area)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (area.Width is < 1 or > MaxSide || area.Height is < 1 or > MaxSide)
        {
            throw new ArgumentException("The frame size is out of range.", nameof(area));
        }

        var rowBytes = area.Width * 4;
        if (stride < rowBytes || pixels.Length < ((long)stride * (area.Height - 1)) + rowBytes)
        {
            throw new ArgumentException("The pixel buffer is smaller than the frame.", nameof(pixels));
        }

        var columns = TilesAcross(area.Width);
        var tiles = new uint[columns * TilesAcross(area.Height)];
        Array.Fill(tiles, 2166136261u); // FNV-1a offset basis
        for (var y = 0; y < area.Height; y++)
        {
            var row = MemoryMarshal.Cast<byte, uint>(pixels.Slice(y * stride, rowBytes));
            var tileRow = y / TileSize * columns;
            for (var column = 0; column < columns; column++)
            {
                var end = Math.Min((column + 1) * TileSize, area.Width);
                var hash = tiles[tileRow + column];
                for (var x = column * TileSize; x < end; x++)
                {
                    hash = (hash ^ (row[x] & 0x00FFFFFFu)) * 16777619u;
                }

                tiles[tileRow + column] = hash;
            }
        }

        return new AgentFrameSignature(area, tiles);
    }

    public string Encode()
    {
        var bytes = new byte[_tiles.Length * 4];
        for (var index = 0; index < _tiles.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), _tiles[index]);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}{Area.X},{Area.Y},{Area.Width},{Area.Height}:{Convert.ToBase64String(bytes)}");
    }

    /// <summary>Reads an encoded signature; anything malformed (it comes from a caller or the helper) is refused.</summary>
    public static bool TryDecode(string? text, [NotNullWhen(true)] out AgentFrameSignature? signature)
    {
        signature = null;
        if (text is null || text.Length > MaxEncodedLength || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = text.IndexOf(':', Prefix.Length);
        if (separator < 0)
        {
            return false;
        }

        var numbers = text[Prefix.Length..separator].Split(',');
        var values = new int[4];
        if (numbers.Length != values.Length)
        {
            return false;
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (!int.TryParse(numbers[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[index]))
            {
                return false;
            }
        }

        if (values[2] is < 1 or > MaxSide || values[3] is < 1 or > MaxSide)
        {
            return false;
        }

        var count = TilesAcross(values[2]) * TilesAcross(values[3]);
        var bytes = new byte[count * 4];
        if (!Convert.TryFromBase64String(text[(separator + 1)..], bytes, out var written) || written != bytes.Length)
        {
            return false;
        }

        var tiles = new uint[count];
        for (var index = 0; index < count; index++)
        {
            tiles[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4));
        }

        signature = new AgentFrameSignature(new AgentRegion(values[0], values[1], values[2], values[3]), tiles);
        return true;
    }

    public bool SameFrameAs(AgentFrameSignature other) =>
        other.Area == Area && other._tiles.AsSpan().SequenceEqual(_tiles);

    /// <summary>
    /// Tile-aligned rectangles (real screen pixels) covering every tile that differs from <paramref name="previous"/>.
    /// Changes at most one unchanged tile apart are grouped together. Empty when nothing changed; null when the two
    /// frames cover different screens and cannot be compared. More than <paramref name="maxRegions"/> groups collapse
    /// into one bounding box.
    /// </summary>
    public IReadOnlyList<AgentRegion>? ChangedRegions(AgentFrameSignature previous, int maxRegions)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (previous.Area != Area)
        {
            return null;
        }

        var changed = new bool[_tiles.Length];
        var any = false;
        for (var index = 0; index < _tiles.Length; index++)
        {
            changed[index] = _tiles[index] != previous._tiles[index];
            any |= changed[index];
        }

        if (!any)
        {
            return [];
        }

        var boxes = new List<TileBox>();
        var visited = new bool[_tiles.Length];
        var pending = new Stack<int>();
        for (var start = 0; start < _tiles.Length; start++)
        {
            if (!changed[start] || visited[start])
            {
                continue;
            }

            visited[start] = true;
            pending.Push(start);
            var box = new TileBox(start % Columns, start / Columns, start % Columns, start / Columns);
            while (pending.TryPop(out var index))
            {
                int column = index % Columns, row = index / Columns;
                box = box.Including(column, row);
                for (var neighborRow = Math.Max(0, row - 2); neighborRow <= Math.Min(Rows - 1, row + 2); neighborRow++)
                {
                    for (var neighborColumn = Math.Max(0, column - 2); neighborColumn <= Math.Min(Columns - 1, column + 2); neighborColumn++)
                    {
                        var neighbor = (neighborRow * Columns) + neighborColumn;
                        if (changed[neighbor] && !visited[neighbor])
                        {
                            visited[neighbor] = true;
                            pending.Push(neighbor);
                        }
                    }
                }
            }

            boxes.Add(box);
        }

        MergeOverlapping(boxes);
        if (boxes.Count > Math.Max(1, maxRegions))
        {
            boxes = [boxes.Aggregate((left, right) => left.Union(right))];
        }

        return boxes
            .OrderBy(box => box.Top)
            .ThenBy(box => box.Left)
            .Select(ToPixels)
            .ToArray();
    }

    /// <summary>Grows a change region by <paramref name="padding"/> pixels of context without leaving the frame.</summary>
    public AgentRegion Pad(AgentRegion region, int padding)
    {
        var left = Math.Max(Area.X, region.X - padding);
        var top = Math.Max(Area.Y, region.Y - padding);
        var right = Math.Min(Area.X + Area.Width, region.X + region.Width + padding);
        var bottom = Math.Min(Area.Y + Area.Height, region.Y + region.Height + padding);
        return new AgentRegion(left, top, right - left, bottom - top);
    }

    /// <summary>Whether crops of these regions save anything over looking at the whole screen.</summary>
    public bool WorthCropping(IReadOnlyList<AgentRegion> regions) =>
        regions.Sum(region => (long)region.Width * region.Height) <= (long)Area.Width * Area.Height * MaxCropShare;

    private static int TilesAcross(int pixels) => (pixels + TileSize - 1) / TileSize;

    private AgentRegion ToPixels(TileBox box)
    {
        var left = box.Left * TileSize;
        var top = box.Top * TileSize;
        return new AgentRegion(
            Area.X + left,
            Area.Y + top,
            Math.Min((box.Right + 1) * TileSize, Area.Width) - left,
            Math.Min((box.Bottom + 1) * TileSize, Area.Height) - top);
    }

    private static void MergeOverlapping(List<TileBox> boxes)
    {
        for (var merged = true; merged;)
        {
            merged = false;
            for (var first = 0; first < boxes.Count && !merged; first++)
            {
                for (var second = first + 1; second < boxes.Count; second++)
                {
                    if (boxes[first].Overlaps(boxes[second]))
                    {
                        boxes[first] = boxes[first].Union(boxes[second]);
                        boxes.RemoveAt(second);
                        merged = true;
                        break;
                    }
                }
            }
        }
    }

    private readonly record struct TileBox(int Left, int Top, int Right, int Bottom)
    {
        internal TileBox Including(int column, int row) =>
            new(Math.Min(Left, column), Math.Min(Top, row), Math.Max(Right, column), Math.Max(Bottom, row));

        internal TileBox Union(TileBox other) =>
            new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

        internal bool Overlaps(TileBox other) =>
            Left <= other.Right && other.Left <= Right && Top <= other.Bottom && other.Top <= Bottom;
    }
}
