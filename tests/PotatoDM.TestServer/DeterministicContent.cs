using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PotatoDM.TestServer;

/// <summary>
/// Stable pseudo-random content derived from a seed. The stream is independent
/// of how it is chunked, so any byte range can be generated on demand.
/// </summary>
public static class DeterministicContent
{
    public static ulong SeedFor(string token, long size, int version)
    {
        var hash = 14695981039346656037UL;

        foreach (var c in token)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        hash ^= (ulong)size;
        hash *= 1099511628211UL;
        hash ^= (ulong)(uint)version;
        hash *= 1099511628211UL;

        return hash == 0 ? 0x9E3779B97F4A7C15UL : hash;
    }

    public static ulong Next(ref ulong state)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return state;
    }

    public static string ComputeSha256(ulong seed, long size)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[64 * 1024];
        var stream = new ContentStream(seed, size, 0);
        long remaining = size;

        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            var read = stream.Read(buffer.AsSpan(0, chunk));
            if (read <= 0)
            {
                break;
            }

            sha.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}

/// <summary>Sequential reader over the deterministic byte stream, starting at an arbitrary offset.</summary>
public sealed class ContentStream
{
    private readonly long _length;
    private ulong _state;
    private long _wordStart;
    private ulong _currentWord;
    private long _position;

    public ContentStream(ulong seed, long length, long start)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(start);

        if (start > length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        _length = length;
        _state = seed;

        var aligned = start & ~7L;
        for (var index = 0L; index < aligned / 8; index++)
        {
            DeterministicContent.Next(ref _state);
        }

        _wordStart = aligned;
        _currentWord = DeterministicContent.Next(ref _state);
        _position = start;
    }

    public long Position => _position;

    public int Read(Span<byte> destination)
    {
        var written = 0;

        while (written < destination.Length && _position < _length)
        {
            var offsetInWord = _position - _wordStart;
            if (offsetInWord >= 8)
            {
                AdvanceWord();
                offsetInWord = 0;
            }

            destination[written++] = (byte)(_currentWord >> (int)(offsetInWord * 8));
            _position++;

            if (offsetInWord == 7 && _position < _length)
            {
                AdvanceWord();
            }
        }

        return written;
    }

    private void AdvanceWord()
    {
        _wordStart += 8;
        _currentWord = DeterministicContent.Next(ref _state);
    }
}
