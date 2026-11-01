using System.Text;

namespace PotatoDM.BrowserIpc;

/// <summary>Reads one newline-terminated UTF-8 message without trusting its length.</summary>
internal static class BoundedLineReader
{
    public static async Task<string?> ReadLineAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var accumulated = new MemoryStream();

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (accumulated.Length == 0)
                {
                    return null;
                }

                throw new EndOfStreamException("The pipe closed in the middle of a message.");
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var take = newline >= 0 ? newline : read;

            if (accumulated.Length + take > maxBytes)
            {
                throw new InvalidDataException($"Pipe message exceeds the {maxBytes}-byte limit.");
            }

            accumulated.Write(buffer, 0, take);

            if (newline >= 0)
            {
                var bytes = accumulated.ToArray();
                if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
                {
                    bytes = bytes[..^1];
                }

                return Encoding.UTF8.GetString(bytes);
            }
        }
    }
}
