using System.Text.Json.Serialization;

namespace PotatoDM.Core.Models;

/// <summary>
/// One contiguous byte range of the resource. <see cref="BytesDone"/> is advanced
/// only after the corresponding bytes have been written to the working file.
/// </summary>
public sealed class DownloadSegment
{
    private long _bytesDone;
    private long _endExclusive;

    public int Index { get; init; }

    public long Start { get; init; }

    /// <summary>
    /// Exclusive end offset. It can shrink while the segment is in flight when a
    /// free connection takes over the tail (see <see cref="SplitLock"/>).
    /// </summary>
    public long EndExclusive
    {
        get => Interlocked.Read(ref _endExclusive);
        init => _endExclusive = value;
    }

    /// <summary>Serializes splitting this range with accounting the bytes it writes.</summary>
    internal object SplitLock { get; } = new();

    public long BytesDone
    {
        get => Interlocked.Read(ref _bytesDone);
        set => Interlocked.Exchange(ref _bytesDone, value);
    }

    [JsonIgnore]
    public long Length => EndExclusive - Start;

    [JsonIgnore]
    public long Remaining => Length - BytesDone;

    [JsonIgnore]
    public bool IsComplete => BytesDone >= Length;

    [JsonIgnore]
    public long NextOffset => Start + BytesDone;

    public long AddBytesDone(long delta) => Interlocked.Add(ref _bytesDone, delta);

    /// <summary>
    /// Accounts bytes just written, never past <see cref="EndExclusive"/>. A split
    /// can lower the boundary after the write started; the helper segment then
    /// re-fetches the tail, so only the committed part may be counted.
    /// </summary>
    public long CommitBytes(long written)
    {
        lock (SplitLock)
        {
            var allowed = EndExclusive - NextOffset;
            if (written > allowed)
            {
                written = allowed;
            }

            if (written <= 0)
            {
                return 0;
            }

            Interlocked.Add(ref _bytesDone, written);
            return written;
        }
    }

    /// <summary>Moves the boundary left; called only while holding <see cref="SplitLock"/>.</summary>
    internal void MoveEndExclusive(long value) => Interlocked.Exchange(ref _endExclusive, value);
}
