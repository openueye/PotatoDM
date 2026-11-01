using PotatoDM.Core.Models;
using PotatoDM.Core.Planning;

namespace PotatoDM.Core.Tests;

public class SegmentSplitterTests
{
    [Fact]
    public void Splits_the_unclaimed_remainder_in_half()
    {
        var target = new DownloadSegment { Index = 0, Start = 0, EndExclusive = 1000 };
        target.BytesDone = 200;

        var helper = SegmentSplitter.TrySplit(target, minHalfBytes: 10, helperIndex: 7);

        Assert.NotNull(helper);
        Assert.Equal(7, helper!.Index);
        Assert.Equal(600, helper.Start);
        Assert.Equal(1000, helper.EndExclusive);
        Assert.Equal(600, target.EndExclusive);
        Assert.Equal(200, target.BytesDone);
    }

    [Fact]
    public void Split_keeps_absolute_offsets_for_a_later_segment()
    {
        var target = new DownloadSegment
        {
            Index = 2, Start = 1000, EndExclusive = 2000, BytesDone = 400,
        };

        var helper = SegmentSplitter.TrySplit(target, minHalfBytes: 100, helperIndex: 3);

        Assert.NotNull(helper);
        Assert.Equal(1700, helper!.Start);
        Assert.Equal(2000, helper.EndExclusive);
        Assert.Equal(1700, target.EndExclusive);
    }

    [Fact]
    public void Refuses_to_create_halves_below_the_minimum()
    {
        var target = new DownloadSegment { Index = 0, Start = 0, EndExclusive = 1000 };
        target.BytesDone = 950;

        Assert.Null(SegmentSplitter.TrySplit(target, minHalfBytes: 30, helperIndex: 1));
        Assert.Equal(1000, target.EndExclusive);
    }

    [Fact]
    public void Refuses_a_completed_segment()
    {
        var target = new DownloadSegment { Index = 0, Start = 0, EndExclusive = 10 };
        target.BytesDone = 10;

        Assert.Null(SegmentSplitter.TrySplit(target, minHalfBytes: 1, helperIndex: 1));
    }

    [Fact]
    public void Commit_never_counts_past_a_shrunk_boundary()
    {
        var target = new DownloadSegment { Index = 0, Start = 0, EndExclusive = 1000 };

        Assert.NotNull(SegmentSplitter.TrySplit(target, minHalfBytes: 100, helperIndex: 1));
        Assert.Equal(500, target.EndExclusive);

        Assert.Equal(400, target.CommitBytes(400));
        Assert.Equal(100, target.CommitBytes(400));
        Assert.Equal(0, target.CommitBytes(1));
        Assert.Equal(500, target.BytesDone);
        Assert.True(target.IsComplete);
    }

    [Fact]
    public async Task Concurrent_splits_leave_a_gap_free_map()
    {
        const long total = 256 * 1024;
        const int chunk = 4096;
        var root = new DownloadSegment { Index = 0, Start = 0, EndExclusive = total };
        var helpers = new List<DownloadSegment>();

        var first = SegmentSplitter.TrySplit(root, minHalfBytes: chunk, helperIndex: 1);
        Assert.NotNull(first);
        helpers.Add(first!);

        var splitter = Task.Run(() =>
        {
            for (var index = 2; index <= 40; index++)
            {
                var helper = SegmentSplitter.TrySplit(root, minHalfBytes: chunk, index);
                if (helper is not null)
                {
                    lock (helpers)
                    {
                        helpers.Add(helper);
                    }
                }

                Thread.Yield();
            }
        });

        while (!root.IsComplete)
        {
            root.CommitBytes(chunk);
            Thread.Yield();
        }

        await splitter;

        List<DownloadSegment> all;
        lock (helpers)
        {
            all = helpers.Prepend(root).OrderBy(segment => segment.Start).ToList();
        }

        Assert.True(all.Count > 1, "the splitter never produced a helper range");

        var covered = 0L;
        var downloaded = 0L;

        foreach (var segment in all)
        {
            Assert.Equal(covered, segment.Start);
            Assert.InRange(segment.BytesDone, 0, segment.Length);
            covered = segment.EndExclusive;
            downloaded += segment.BytesDone;

            // The worker that owns a helper would finish it; emulate that here.
            segment.CommitBytes(segment.Remaining);
            Assert.True(segment.IsComplete);
        }

        Assert.Equal(total, covered);
        Assert.Equal(total, all.Sum(segment => segment.BytesDone));
        Assert.True(downloaded <= total);
    }
}
