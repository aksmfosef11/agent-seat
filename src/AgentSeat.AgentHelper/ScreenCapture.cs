using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using AgentSeat.Core.Agent;

namespace AgentSeat.AgentHelper;

/// <summary>A failure the helper can name for the caller (for example coordinates off the screen).</summary>
internal sealed class AgentActionException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    internal string Code { get; } = code;
}

internal readonly record struct DesktopBounds(int X, int Y, int Width, int Height)
{
    internal bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

internal static class ScreenCapture
{
    private const int MaxChangeRegions = 4;
    private const int CropPadding = 16;
    private const int StablePollMilliseconds = 100;

    /// <summary>The bounding box of every monitor in this session; a seat session usually has one.</summary>
    internal static DesktopBounds GetDesktop()
    {
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);
        if (width > 0 && height > 0)
        {
            return new DesktopBounds(
                NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
                NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
                width,
                height);
        }

        return new DesktopBounds(
            0,
            0,
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyScreen));
    }

    internal static JsonObject Capture(AgentRequest request)
    {
        var desktop = GetDesktop();
        if (desktop.Width < 1 || desktop.Height < 1)
        {
            throw new AgentActionException("screen_unavailable", "The session reports no display.");
        }

        var area = desktop;
        if (request.Region is { } region)
        {
            var left = Math.Max(desktop.X, region.X);
            var top = Math.Max(desktop.Y, region.Y);
            var right = Math.Min(desktop.X + desktop.Width, region.X + region.Width);
            var bottom = Math.Min(desktop.Y + desktop.Height, region.Y + region.Height);
            if (right <= left || bottom <= top)
            {
                throw new AgentActionException("out_of_bounds", "The requested region is outside the screen.");
            }

            area = new DesktopBounds(left, top, right - left, bottom - top);
        }

        var scale = request.Scale ?? 1.0;
        var outputWidth = Math.Max(1, (int)Math.Round(area.Width * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(area.Height * scale));
        var jpeg = string.Equals(request.Format, "jpeg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(request.Format, "jpg", StringComparison.OrdinalIgnoreCase);

        // Change reports compare whole screens; a region (zoom) capture never takes part.
        var wantSignature = request.Region is null && request.Diff == true;
        var settled = CaptureSettled(area, request.StableMilliseconds ?? 0, wantSignature);
        using var capture = settled.Frame;
        var signature = settled.Signature;
        bool? changed = null;
        IReadOnlyList<AgentRegion>? changes = null;
        if (signature is not null && AgentFrameSignature.TryDecode(request.Since, out var previous) &&
            signature.ChangedRegions(previous, MaxChangeRegions) is { } regions)
        {
            changed = regions.Count > 0;
            changes = [.. regions.Select(region => signature.Pad(region, CropPadding))];
        }

        if (request.Cursor ?? true)
        {
            using var graphics = Graphics.FromImage(capture);
            DrawCursor(graphics, area);
        }

        using var output = outputWidth == area.Width && outputHeight == area.Height
            ? null
            : Resize(capture, outputWidth, outputHeight);
        var image = output ?? capture;

        using var stream = new MemoryStream();
        if (jpeg)
        {
            SaveJpeg(image, stream, request.Quality ?? 85);
        }
        else
        {
            image.Save(stream, ImageFormat.Png);
            if (!FitsInOneMessage(stream.Length))
            {
                // A very noisy large screen can compress poorly as PNG; JPEG keeps it inside the pipe limit.
                stream.SetLength(0);
                SaveJpeg(image, stream, request.Quality ?? 80);
                jpeg = true;
            }
        }

        if (!FitsInOneMessage(stream.Length))
        {
            throw new AgentActionException(
                "screenshot_too_large",
                "The screenshot is too large to return even as JPEG. Use scale below 1 or a region.");
        }

        _ = NativeMethods.GetCursorPos(out var cursor);
        var image64 = Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
        var result = new JsonObject
        {
            ["mimeType"] = jpeg ? "image/jpeg" : "image/png",
            ["width"] = outputWidth,
            ["height"] = outputHeight,
            ["desktopWidth"] = desktop.Width,
            ["desktopHeight"] = desktop.Height,
            ["originX"] = desktop.X,
            ["originY"] = desktop.Y,
            ["regionX"] = area.X,
            ["regionY"] = area.Y,
            ["regionWidth"] = area.Width,
            ["regionHeight"] = area.Height,
            ["cursorX"] = cursor.X,
            ["cursorY"] = cursor.Y,
            ["dataBase64"] = image64
        };
        var encodedSignature = signature?.Encode();
        if (encodedSignature is not null)
        {
            result["signature"] = encodedSignature;
        }

        if (changed is not null)
        {
            result["changed"] = changed;
        }

        if (changes is { Count: > 0 })
        {
            result["changes"] = DescribeChanges(
                capture,
                area,
                signature!,
                changes,
                budget: AgentFraming.MaxMessageBytes - 65_536 - image64.Length - (encodedSignature?.Length ?? 0));
        }

        return result;
    }

    /// <summary>
    /// Lists the changed areas and, when together they are small next to the screen, adds a full-resolution PNG
    /// crop of each so the agent can look at a few hundred pixels instead of the whole screen.
    /// </summary>
    private static JsonArray DescribeChanges(
        Bitmap frame,
        DesktopBounds area,
        AgentFrameSignature signature,
        IReadOnlyList<AgentRegion> changes,
        long budget)
    {
        var crop = signature.WorthCropping(changes);
        var described = new JsonArray();
        foreach (var change in changes)
        {
            var entry = new JsonObject
            {
                ["x"] = change.X,
                ["y"] = change.Y,
                ["width"] = change.Width,
                ["height"] = change.Height
            };
            if (crop)
            {
                using var piece = frame.Clone(
                    new Rectangle(change.X - area.X, change.Y - area.Y, change.Width, change.Height),
                    PixelFormat.Format32bppArgb);
                using var stream = new MemoryStream();
                piece.Save(stream, ImageFormat.Png);
                var piece64 = Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
                if (piece64.Length < budget)
                {
                    budget -= piece64.Length;
                    entry["mimeType"] = "image/png";
                    entry["dataBase64"] = piece64;
                }
            }

            described.Add(entry);
        }

        return described;
    }

    /// <summary>
    /// Grabs the area. With a stable wait it keeps grabbing until two grabs in a row are identical (the UI has
    /// stopped moving) or the wait runs out, and keeps the last grab, so the agent does not get a half-drawn screen
    /// and come back for another one. The fingerprint is taken before the cursor is painted in, so a pointer that
    /// only moved does not count as a change.
    /// </summary>
    private static (Bitmap Frame, AgentFrameSignature? Signature) CaptureSettled(
        DesktopBounds area,
        int stableMilliseconds,
        bool wantSignature)
    {
        var frame = Grab(area);
        try
        {
            if (stableMilliseconds <= 0 && !wantSignature)
            {
                return (frame, null);
            }

            var signature = Fingerprint(frame, area);
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < stableMilliseconds)
            {
                Thread.Sleep(StablePollMilliseconds);
                var next = Grab(area);
                frame.Dispose();
                frame = next;
                var nextSignature = Fingerprint(frame, area);
                var settled = nextSignature.SameFrameAs(signature);
                signature = nextSignature;
                if (settled)
                {
                    break;
                }
            }

            return (frame, wantSignature ? signature : null);
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private static Bitmap Grab(DesktopBounds area)
    {
        var frame = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(frame);
            BitBltScreen(graphics, area);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private static AgentFrameSignature Fingerprint(Bitmap frame, DesktopBounds area)
    {
        var data = frame.LockBits(
            new Rectangle(0, 0, area.Width, area.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        var length = data.Stride * area.Height;
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, length);
            return AgentFrameSignature.Compute(
                pixels.AsSpan(0, length),
                data.Stride,
                new AgentRegion(area.X, area.Y, area.Width, area.Height));
        }
        finally
        {
            frame.UnlockBits(data);
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    // The image travels base64-encoded (4/3 larger) inside one pipe message with a hard size limit, next to a
    // frame signature of at most AgentFrameSignature.MaxEncodedLength characters.
    private static bool FitsInOneMessage(long imageBytes) =>
        imageBytes * 4 / 3 + 4096 + AgentFrameSignature.MaxEncodedLength < AgentFraming.MaxMessageBytes;

    /// <summary>
    /// Copies the screen with GDI directly: <c>Graphics.CopyFromScreen</c> rejects the CAPTUREBLT flag
    /// that is needed to include layered (translucent) windows such as menus and tooltips.
    /// </summary>
    private static void BitBltScreen(Graphics target, DesktopBounds area)
    {
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            throw ScreenUnavailable(null);
        }

        var destination = target.GetHdc();
        try
        {
            if (!NativeMethods.BitBlt(
                    destination,
                    0,
                    0,
                    area.Width,
                    area.Height,
                    screen,
                    area.X,
                    area.Y,
                    NativeMethods.SrcCopy | NativeMethods.CaptureBlt))
            {
                throw ScreenUnavailable(new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        finally
        {
            target.ReleaseHdc(destination);
            _ = NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static AgentActionException ScreenUnavailable(Exception? inner) => new(
        "screen_unavailable",
        "The session screen cannot be read right now (locked, disconnected or showing a secure prompt).",
        inner);

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        return resized;
    }

    private static void SaveJpeg(Image image, Stream stream, int quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(candidate => candidate.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        image.Save(stream, codec, parameters);
    }

    /// <summary>GDI screen capture leaves the pointer out, so it is painted in where it really is.</summary>
    private static void DrawCursor(Graphics graphics, DesktopBounds area)
    {
        var info = new NativeMethods.CursorInfo { Size = Marshal.SizeOf<NativeMethods.CursorInfo>() };
        if (!NativeMethods.GetCursorInfo(ref info) || (info.Flags & NativeMethods.CursorShowing) == 0 ||
            info.Cursor == IntPtr.Zero)
        {
            return;
        }

        var hotspotX = 0;
        var hotspotY = 0;
        if (NativeMethods.GetIconInfo(info.Cursor, out var icon))
        {
            hotspotX = icon.HotspotX;
            hotspotY = icon.HotspotY;
            if (icon.MaskBitmap != IntPtr.Zero)
            {
                _ = NativeMethods.DeleteObject(icon.MaskBitmap);
            }

            if (icon.ColorBitmap != IntPtr.Zero)
            {
                _ = NativeMethods.DeleteObject(icon.ColorBitmap);
            }
        }

        var deviceContext = graphics.GetHdc();
        try
        {
            _ = NativeMethods.DrawIconEx(
                deviceContext,
                info.ScreenPosition.X - area.X - hotspotX,
                info.ScreenPosition.Y - area.Y - hotspotY,
                info.Cursor,
                0,
                0,
                0,
                IntPtr.Zero,
                NativeMethods.DiNormal);
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }
    }
}
