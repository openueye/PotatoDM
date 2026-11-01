using PotatoDM.Core.Models;

namespace PotatoDM.Core.Planning;

/// <summary>
/// Moves the tail of an in-flight range to a helper range. The owner keeps
/// <c>[Start, split)</c> and the helper receives <c>[split, oldEnd)</c>, so the
/// segment map stays gap-free and a resume never has to guess.
/// </summary>
public static class SegmentSplitter
{
    /// <summary>
    /// Splits <paramref name="target"/> in half if both halves would be at least
    /// <paramref name="minHalfBytes"/>. Returns the helper range, or null when the
    /// range is too small to divide.
    /// </summary>
    public static DownloadSegment? TrySplit(
        DownloadSegment target,
        long minHalfBytes,
        int helperIndex)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(minHalfBytes, 1);

        lock (target.SplitLock)
        {
            var end = target.EndExclusive;
            var next = target.NextOffset;
            var remaining = end - next;

            if (remaining < 2 || remaining / 2 < minHalfBytes)
            {
                return null;
            }

            var split = next + remaining / 2;
            target.MoveEndExclusive(split);

            return new DownloadSegment
            {
                Index = helperIndex,
                Start = split,
                EndExclusive = end,
            };
        }
    }
}
