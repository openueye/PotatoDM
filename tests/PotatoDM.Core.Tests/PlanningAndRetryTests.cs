using System.Net;
using PotatoDM.Core.Models;
using PotatoDM.Core.Planning;
using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Tests;

public class SegmentPlannerTests
{
    [Fact]
    public void Zero_length_yields_one_empty_segment()
    {
        var segments = SegmentPlanner.Plan(0, true, 8, 1, 1);

        var segment = Assert.Single(segments);
        Assert.Equal(0, segment.Start);
        Assert.Equal(0, segment.EndExclusive);
    }

    [Theory]
    [InlineData(false, 8, 1, 1)]
    [InlineData(true, 1, 1, 1)]
    [InlineData(true, 8, 1, 1000)]
    public void Degrades_to_single_stream(
        bool supportsRanges,
        int connections,
        long minSegmentSize,
        long minBytesForSegmented)
    {
        var segments = SegmentPlanner.Plan(
            1000, supportsRanges, connections, minSegmentSize, minBytesForSegmented);

        var segment = Assert.Single(segments);
        Assert.Equal(0, segment.Start);
        Assert.Equal(1000, segment.EndExclusive);
    }

    [Fact]
    public void Splits_evenly_when_size_divides()
    {
        var segments = SegmentPlanner.Plan(100, true, 4, 10, 2);

        Assert.Equal(4, segments.Count);
        Assert.Equal(new[] { (0L, 25L), (25L, 50L), (50L, 75L), (75L, 100L) },
            segments.Select(s => (s.Start, s.EndExclusive)).ToArray());
    }

    [Fact]
    public void Distributes_remainder_to_leading_segments()
    {
        var segments = SegmentPlanner.Plan(10, true, 3, 1, 2);

        Assert.Equal(new[] { (0L, 4L), (4L, 7L), (7L, 10L) },
            segments.Select(s => (s.Start, s.EndExclusive)).ToArray());
    }

    [Fact]
    public void Minimum_segment_size_limits_connection_count()
    {
        var segments = SegmentPlanner.Plan(16, true, 8, 8, 1);

        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Equal(8, s.Length));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(7L)]
    [InlineData(8L)]
    [InlineData(4095L)]
    [InlineData(4096L)]
    [InlineData(1024L * 1024 - 1)]
    [InlineData(1024L * 1024)]
    [InlineData(1024L * 1024 + 1)]
    [InlineData(8L * 1024 * 1024)]
    public void Segments_are_contiguous_and_cover_the_whole_resource(long total)
    {
        foreach (var connections in new[] { 1, 2, 3, 8, 16 })
        {
            var segments = SegmentPlanner.Plan(
                total, true, connections, 4096, 2 * 1024 * 1024);

            var expectedStart = 0L;
            for (var index = 0; index < segments.Count; index++)
            {
                Assert.Equal(index, segments[index].Index);
                Assert.Equal(expectedStart, segments[index].Start);
                Assert.True(segments[index].EndExclusive >= segments[index].Start);
                expectedStart = segments[index].EndExclusive;
            }

            Assert.Equal(total, expectedStart);
        }
    }

    [Fact]
    public void Single_helper_covers_full_resource()
    {
        var segment = Assert.Single(SegmentPlanner.SingleSegment(123));

        Assert.Equal(0, segment.Start);
        Assert.Equal(123, segment.EndExclusive);
    }
}

public class RetryPolicyTests
{
    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(599, true)]
    [InlineData(400, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(416, false)]
    public void Classifies_retryable_status_codes(int code, bool expected)
    {
        Assert.Equal(expected, RetryPolicy.IsRetryableStatus((HttpStatusCode)code));
    }

    [Theory]
    [InlineData(0, 0.0, 200)]
    [InlineData(0, 0.5, 250)]
    [InlineData(0, 1.0, 300)]
    [InlineData(2, 0.5, 1000)]
    [InlineData(3, 0.5, 2000)]
    public void Exponential_backoff_applies_bounded_jitter(int attempt, double jitter, int expectedMs)
    {
        var policy = new RetryPolicy(5, TimeSpan.FromSeconds(30), () => jitter);

        Assert.Equal(expectedMs, policy.GetDelay(attempt, null).TotalMilliseconds, 3);
    }

    [Fact]
    public void Backoff_never_exceeds_the_maximum_delay()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromSeconds(1), () => 1.0);

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(20, null));
    }

    [Fact]
    public void Retry_after_wins_over_backoff()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromSeconds(30), () => 1.0);

        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(0, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Retry_after_is_capped()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(10), policy.GetDelay(0, TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_retry_after_means_retry_immediately(int seconds)
    {
        var policy = new RetryPolicy(5, TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.Zero, policy.GetDelay(0, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Max_retries_is_never_negative()
    {
        Assert.Equal(0, new RetryPolicy(-3).MaxRetries);
    }
}
