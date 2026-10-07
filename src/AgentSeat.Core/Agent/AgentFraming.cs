using System.Buffers.Binary;
using System.Text.Json;

namespace AgentSeat.Core.Agent;

/// <summary>
/// Length-prefixed JSON messages (4-byte little-endian length, then UTF-8 payload) spoken over the
/// service ↔ helper named pipe. Screenshots can be megabytes, so newline framing is avoided.
/// </summary>
public static class AgentFraming
{
    public const int MaxMessageBytes = 32 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Agent message of {payload.Length} bytes exceeds the {MaxMessageBytes} byte limit.");
        }

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsMemory(4));
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; returns <see langword="null"/> when the peer closed cleanly between frames.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var headerRead = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < header.Length)
        {
            throw new InvalidDataException("The agent pipe closed in the middle of a message header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException($"Agent message length {length} is outside 0..{MaxMessageBytes}.");
        }

        var payload = new byte[length];
        if (await ReadFullyAsync(stream, payload, cancellationToken).ConfigureAwait(false) < length)
        {
            throw new InvalidDataException("The agent pipe closed in the middle of a message.");
        }

        return payload;
    }

    public static Task WriteJsonAsync<T>(Stream stream, T message, CancellationToken cancellationToken) =>
        WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(message, AgentJson.Options), cancellationToken);

    public static async Task<T?> ReadJsonAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var payload = await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        return payload is null ? default : JsonSerializer.Deserialize<T>(payload, AgentJson.Options);
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
