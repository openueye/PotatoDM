using PotatoDM.Core.Models;
using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Tests;

public class SpeedMeterTests
{
    [Fact]
    public void Reports_zero_until_two_samples_exist()
    {
        var meter = new SpeedMeter();
        var start = DateTimeOffset.UnixEpoch;

        Assert.Equal(0, meter.BytesPerSecond);
        meter.Add(0, start);
        Assert.Equal(0, meter.BytesPerSecond);
    }

    [Fact]
    public void Reports_bytes_over_elapsed_seconds()
    {
        var meter = new SpeedMeter(TimeSpan.FromSeconds(2));
        var start = DateTimeOffset.UnixEpoch;

        meter.Add(0, start);
        meter.Add(1000, start.AddSeconds(1));

        Assert.Equal(1000, meter.BytesPerSecond, 3);
    }

    [Fact]
    public void Drops_samples_outside_the_window()
    {
        var meter = new SpeedMeter(TimeSpan.FromSeconds(2));
        var start = DateTimeOffset.UnixEpoch;

        meter.Add(0, start);
        meter.Add(1000, start.AddSeconds(1));
        meter.Add(3000, start.AddSeconds(2.1));

        Assert.Equal(2000 / 1.1, meter.BytesPerSecond, 3);
    }
}

public class ConnectionLimiterTests
{
    private static readonly Uri HostA = new("http://127.0.0.1:5001/file");
    private static readonly Uri HostB = new("http://127.0.0.1:5002/file");
    private static readonly Uri HostC = new("http://127.0.0.1:5003/file");

    private static CancellationToken Timeout(int milliseconds) =>
        new CancellationTokenSource(milliseconds).Token;

    [Fact]
    public async Task Per_host_cap_blocks_additional_connections()
    {
        using var limiter = new ConnectionLimiter(maxGlobal: 4, maxPerHost: 2);

        using var first = await limiter.AcquireAsync(HostA, Timeout(1000));
        using var second = await limiter.AcquireAsync(HostA, Timeout(1000));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await limiter.AcquireAsync(HostA, Timeout(150)));

        // A different authority still has global capacity.
        using var otherHost = await limiter.AcquireAsync(HostB, Timeout(1000));
    }

    [Fact]
    public async Task Global_cap_blocks_across_hosts()
    {
        using var limiter = new ConnectionLimiter(maxGlobal: 1, maxPerHost: 4);

        using var first = await limiter.AcquireAsync(HostA, Timeout(1000));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await limiter.AcquireAsync(HostB, Timeout(150)));
    }

    [Fact]
    public async Task Releasing_a_lease_frees_the_slot()
    {
        using var limiter = new ConnectionLimiter(1, 1);

        var lease = await limiter.AcquireAsync(HostA, Timeout(1000));
        lease.Dispose();

        using var next = await limiter.AcquireAsync(HostA, Timeout(1000));
    }

    [Fact]
    public async Task Disposing_twice_does_not_over_release()
    {
        using var limiter = new ConnectionLimiter(1, 1);

        var lease = await limiter.AcquireAsync(HostA, Timeout(1000));
        lease.Dispose();
        lease.Dispose();

        using var reacquired = await limiter.AcquireAsync(HostA, Timeout(1000));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await limiter.AcquireAsync(HostA, Timeout(150)));
    }

    [Fact]
    public async Task A_failed_host_wait_returns_the_global_permit()
    {
        using var limiter = new ConnectionLimiter(maxGlobal: 3, maxPerHost: 2);

        using var a1 = await limiter.AcquireAsync(HostA, Timeout(1000));
        using var a2 = await limiter.AcquireAsync(HostA, Timeout(1000));

        // Takes the last global permit, then fails on the host cap.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await limiter.AcquireAsync(HostA, Timeout(150)));

        a1.Dispose();
        a2.Dispose();

        // All three global permits must be available again.
        using var b1 = await limiter.AcquireAsync(HostB, Timeout(1000));
        using var b2 = await limiter.AcquireAsync(HostB, Timeout(1000));
        using var c1 = await limiter.AcquireAsync(HostC, Timeout(1000));
    }

    [Fact]
    public async Task Disposed_limiter_rejects_new_leases()
    {
        var limiter = new ConnectionLimiter(1, 1);
        limiter.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await limiter.AcquireAsync(HostA, Timeout(1000)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Rejects_non_positive_limits(int global, int perHost)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConnectionLimiter(global, perHost));
    }
}

public class DownloadSegmentTests
{
    [Fact]
    public void Tracks_offsets_and_completion()
    {
        var segment = new DownloadSegment
        {
            Index = 1, Start = 100, EndExclusive = 200, BytesDone = 10,
        };

        Assert.Equal(100, segment.Length);
        Assert.Equal(90, segment.Remaining);
        Assert.Equal(110, segment.NextOffset);
        Assert.False(segment.IsComplete);

        segment.AddBytesDone(5);
        Assert.Equal(15, segment.BytesDone);

        segment.BytesDone = 100;
        Assert.True(segment.IsComplete);
        Assert.Equal(0, segment.Remaining);
    }
}

public class DownloadProgressTests
{
    [Theory]
    [InlineData(50, 200, 25.0)]
    [InlineData(0, 200, 0.0)]
    [InlineData(400, 200, 100.0)]
    public void Computes_a_clamped_percentage(long done, long total, double expected)
    {
        var progress = new DownloadProgress(done, total, 0, null, 0);

        Assert.Equal(expected, progress.Percent!.Value, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void Percentage_is_unknown_without_a_positive_total(long? total)
    {
        var progress = new DownloadProgress(10, total, 0, null, 0);

        Assert.Null(progress.Percent);
    }
}
