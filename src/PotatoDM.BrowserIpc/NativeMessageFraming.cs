using System.Buffers.Binary;
using System.Text;

namespace PotatoDM.BrowserIpc;

/// <summary>
/// Chrome/Edge native messaging framing: a 32-bit little-endian length
/// followed by that many bytes of UTF-8 JSON.
/// </summary>
public static class NativeMessageFraming
{
    public const int MaxMessageBytes = 1024 * 1024;

    public static async Task WriteAsync(
        Stream stream,
        string json,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(json);

        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"Native message is {payload.Length} bytes; the limit is {MaxMessageBytes}.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"Native message length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        return Encoding.UTF8.GetString(payload);
    }
}
