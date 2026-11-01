using PotatoDM.Core.Models;

namespace PotatoDM.Core.Planning;

/// <summary>
/// Splits a resource into contiguous, gap-free byte ranges. Small resources or
/// servers without Range support collapse to one segment.
/// </summary>
public static class SegmentPlanner
{
    public static IReadOnlyList<DownloadSegment> Plan(
        long totalLength,
        bool supportsRanges,
        int connections,
        long minSegmentSize,
        long minBytesForSegmented)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(connections, 1);

        if (totalLength == 0)
        {
            return new[] { new DownloadSegment { Index = 0, Start = 0, EndExclusive = 0 } };
        }

        if (!supportsRanges || connections == 1 || totalLength <= minBytesForSegmented)
        {
            return SingleSegment(totalLength);
        }

        var maxByMinSize = minSegmentSize > 0
            ? totalLength / minSegmentSize
            : connections;

        var count = (int)Math.Min((long)connections, Math.Max(1L, maxByMinSize));
        if (count <= 1)
        {
            return SingleSegment(totalLength);
        }

        var baseSize = totalLength / count;
        var remainder = totalLength % count;
        var segments = new List<DownloadSegment>(count);
        var offset = 0L;

        for (var index = 0; index < count; index++)
        {
            var length = baseSize + (index < remainder ? 1 : 0);
            segments.Add(new DownloadSegment
            {
                Index = index,
                Start = offset,
                EndExclusive = offset + length,
            });
            offset += length;
        }

        return segments;
    }

    public static IReadOnlyList<DownloadSegment> SingleSegment(long totalLength)
    {
        return new[] { new DownloadSegment { Index = 0, Start = 0, EndExclusive = totalLength } };
    }
}
