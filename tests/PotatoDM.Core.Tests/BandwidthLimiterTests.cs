using System.Diagnostics;
using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Tests;

public class BandwidthLimiterTests
{
    [Fact]
    public async Task Zero_rate_does_not_delay()
    {
        var limiter = new BandwidthLimiter(0);
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < 100; i++)
        {
            await limiter.ThrottleAsync(64 * 1024);
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"unlimited limiter took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task First_chunk_is_immediate()
    {
        var limiter = new BandwidthLimiter(1_000);
        var stopwatch = Stopwatch.StartNew();

        await limiter.ThrottleAsync(1_000);

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
            $"first chunk waited {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Concurrent_callers_share_one_rate_budget()
    {
        const int rate = 1_000_000;
        var limiter = new BandwidthLimiter(rate);
        var stopwatch = Stopwatch.StartNew();

        await Task.WhenAll(
            Enumerable.Range(0, 10)
                .Select(_ => limiter.ThrottleAsync(10_000).AsTask()));

        stopwatch.Stop();

        // 100 KiB at 1 MB/s is ~100 ms; the first slot is free, so allow
        // some scheduling slack below that.
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(60),
            $"100 KiB finished in {stopwatch.Elapsed}, faster than the cap allows.");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"limiter stalled for {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Cancellation_interrupts_a_long_wait()
    {
        var limiter = new BandwidthLimiter(1_000);
        await limiter.ThrottleAsync(1_000);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => limiter.ThrottleAsync(10_000, cancellation.Token).AsTask());
    }

    [Fact]
    public void Rate_is_read_from_the_provider_on_every_call()
    {
        long rate = 100;
        var limiter = new BandwidthLimiter(() => rate);

        Assert.Equal(100, limiter.MaxBytesPerSecond);

        rate = 2_000;

        Assert.Equal(2_000, limiter.MaxBytesPerSecond);
    }

    [Fact]
    public void Negative_fixed_rate_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandwidthLimiter(-1));
    }
}
