using System.Collections.Concurrent;
using System.Diagnostics;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;
using PotatoDM.Core.Transfer;

namespace PotatoDM.IntegrationTests;

[Collection(TestServerCollection.Name)]
public class DownloadIntegrationTests
{
    private readonly TestServerFixture _server;

    public DownloadIntegrationTests(TestServerFixture server)
    {
        _server = server;
    }

    private static RequestOptions NoProxy => new() { UseProxy = false };

    private static DownloadRequest Request(
        Uri url,
        string target,
        int connections = 8,
        long minSegmentSize = 1024 * 1024,
        int maxRetries = 3,
        int maxConnectionsPerHost = 8,
        int maxConnectionsPerJob = 64,
        TimeSpan? stallTimeout = null,
        IBandwidthLimiter? bandwidthLimiter = null,
        bool dynamicSegmentation = false)
    {
        return new DownloadRequest
        {
            Url = url,
            TargetPath = target,
            Connections = connections,
            MinSegmentSize = minSegmentSize,
            MaxRetries = maxRetries,
            MaxRetryDelay = TimeSpan.FromSeconds(2),
            MaxConnectionsPerHost = maxConnectionsPerHost,
            MaxConnectionsPerJob = maxConnectionsPerJob,
            StallTimeout = stallTimeout ?? TimeSpan.FromSeconds(10),
            BandwidthLimiter = bandwidthLimiter,
            DynamicSegmentation = dynamicSegmentation,
            RequestOptions = NoProxy,
        };
    }

    private static ResumeOptions Resume(bool restartOnChange = false)
    {
        return new ResumeOptions
        {
            RestartOnChange = restartOnChange,
            MaxRetries = 3,
            MaxRetryDelay = TimeSpan.FromSeconds(2),
            StallTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerHost = 8,
            MaxConnectionsPerJob = 64,
            RequestOptions = NoProxy,
        };
    }

    [Fact]
    public async Task Probe_reports_length_ranges_and_validators()
    {
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size=65536&cd=ascii"));

        var probe = await new DownloadEngine()
            .ProbeAsync(url, NoProxy, IntegrationHelpers.Timeout(30));

        Assert.Equal(65536, probe.TotalLength);
        Assert.True(probe.SupportsRanges);
        Assert.NotNull(probe.ETag);
        Assert.True(probe.HasStrongETag);
        Assert.NotNull(probe.LastModified);
        Assert.Equal("hello.bin", probe.SuggestedFileName);
    }

    [Fact]
    public async Task Segmented_download_matches_checksum_and_uses_parallel_ranges()
    {
        const long size = 4L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&throttle=2097152"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("segmented.bin");

        var engine = new DownloadEngine();
        var progress = new ConcurrentQueue<DownloadProgress>();
        engine.ProgressChanged += (_, value) => progress.Enqueue(value);

        var result = await engine.DownloadAsync(
            Request(url, target, connections: 4, minSegmentSize: 256 * 1024),
            IntegrationHelpers.Timeout(90));

        Assert.Equal(size, result.TotalBytes);
        Assert.Equal(DownloadMode.Segmented, result.Mode);
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));

        var log = await _server.GetLogAsync(token);
        var segments = log.Where(IntegrationHelpers.IsSegment).ToList();

        Assert.Contains(log, IntegrationHelpers.IsProbe);
        Assert.True(segments.Count >= 4, $"expected >= 4 range requests, saw {segments.Count}");
        Assert.True(segments.Select(entry => entry.Range).Distinct().Count() >= 4);
        Assert.All(segments, entry => Assert.Equal(206, entry.Status));
        Assert.All(segments, entry => Assert.False(string.IsNullOrEmpty(entry.IfRange)));
        Assert.True(IntegrationHelpers.PeakConcurrency(segments) >= 2);
        Assert.Contains(progress, value => value.BytesDownloaded == size);
    }

    [Fact]
    public async Task Dynamic_segmentation_splits_a_straggling_range()
    {
        const long size = 8L * 1024 * 1024;
        const long midpoint = size / 2;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url(
            $"/dl?token={token}&size={size}&throttle=16777216" +
            "&slowStart=0&slowDelayMs=700"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("dynamic.bin");

        var result = await new DownloadEngine().DownloadAsync(
            Request(
                url,
                target,
                connections: 2,
                minSegmentSize: 512 * 1024,
                dynamicSegmentation: true),
            IntegrationHelpers.Timeout(90));

        Assert.Equal(size, result.TotalBytes);
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));

        var starts = (await _server.GetLogAsync(token))
            .Where(IntegrationHelpers.IsSegment)
            .Select(entry => IntegrationHelpers.ParseRangeHeader(entry.Range))
            .Where(range => range is not null)
            .Select(range => range!.Value.Start)
            .ToList();

        Assert.True(
            starts.Count >= 3,
            $"expected a stolen helper range, saw starts: {string.Join(", ", starts)}");
        Assert.Contains(starts, start => start > 0 && start != midpoint);
    }

    [Fact]
    public async Task Bandwidth_limit_paces_the_body_transfer()
    {
        const long size = 1024 * 1024;
        const long rate = 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("limited.bin");

        var stopwatch = Stopwatch.StartNew();
        var result = await new DownloadEngine().DownloadAsync(
            Request(
                url,
                target,
                connections: 1,
                bandwidthLimiter: new BandwidthLimiter(rate)),
            IntegrationHelpers.Timeout(30));
        stopwatch.Stop();

        Assert.Equal(size, result.TotalBytes);
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(700),
            $"1 MiB at 1 MiB/s finished in {stopwatch.Elapsed}.");
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));
    }

    [Fact]
    public async Task Small_file_uses_one_segment_request()
    {
        const long size = 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("small.bin");

        var result = await new DownloadEngine().DownloadAsync(
            Request(url, target, minSegmentSize: 256),
            IntegrationHelpers.Timeout(30));

        Assert.Equal(DownloadMode.SingleStream, result.Mode);
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));

        var log = await _server.GetLogAsync(token);
        var segments = log.Where(IntegrationHelpers.IsSegment).ToList();

        Assert.Single(segments);
        Assert.Equal("bytes=0-1023", segments[0].Range);
    }

    [Fact]
    public async Task Range_ignored_after_probe_falls_back_to_single_stream()
    {
        const long size = 3L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(
            _server.Url($"/dl?token={token}&size={size}&range=ignore-later&throttle=4194304"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("fallback.bin");

        var result = await new DownloadEngine().DownloadAsync(
            Request(url, target, connections: 4, minSegmentSize: 256 * 1024),
            IntegrationHelpers.Timeout(90));

        Assert.Equal(DownloadMode.SingleStream, result.Mode);
        Assert.Equal(size, result.TotalBytes);
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));

        var log = await _server.GetLogAsync(token);

        Assert.Contains(log, entry =>
            IntegrationHelpers.IsSegment(entry) && entry.Status == 200);

        var plainGet = Assert.Single(log.Where(entry =>
            entry.Method == "GET" &&
            string.IsNullOrEmpty(entry.Range) &&
            entry.Status == 200));

        Assert.Equal(size, plainGet.BytesSent);
    }

    [Fact]
    public async Task Segment_failure_honors_retry_after()
    {
        const long size = 2L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&throttle=8388608"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("retry-after.bin");

        await _server.ConfigureAsync(
            token, failSegments: 1, failStatus: 429, retryAfter: 1);

        var stopwatch = Stopwatch.StartNew();

        var result = await new DownloadEngine().DownloadAsync(
            Request(url, target, connections: 8, minSegmentSize: 128 * 1024),
            IntegrationHelpers.Timeout(90));

        stopwatch.Stop();

        Assert.Equal(size, result.TotalBytes);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"expected a Retry-After wait, took {stopwatch.Elapsed}");
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));

        var log = await _server.GetLogAsync(token);
        Assert.Contains(log, entry =>
            IntegrationHelpers.IsSegment(entry) && entry.Status == 429);
    }

    [Fact]
    public async Task Stall_watchdog_reconnects_from_the_last_offset()
    {
        const long size = 150_000;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(
            _server.Url($"/dl?token={token}&size={size}&stallAfter=100000&stallMs=2000"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("stall.bin");

        var request = Request(
            url, target, connections: 1, stallTimeout: TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();

        var result = await new DownloadEngine().DownloadAsync(
            request, IntegrationHelpers.Timeout(60));

        stopwatch.Stop();

        Assert.Equal(size, result.TotalBytes);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));

        var segments = (await _server.GetLogAsync(token))
            .Where(IntegrationHelpers.IsSegment)
            .ToList();

        Assert.True(segments.Count >= 2, $"expected a reconnect, saw {segments.Count}");
        Assert.Contains(segments, entry =>
            IntegrationHelpers.ParseRangeHeader(entry.Range) is { Start: > 0 });
    }

    [Fact]
    public async Task Broken_connections_resume_from_the_last_offset()
    {
        const long size = 500_000;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&disconnectEvery=100000"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("disconnect.bin");

        var result = await new DownloadEngine().DownloadAsync(
            Request(url, target, connections: 1),
            IntegrationHelpers.Timeout(60));

        Assert.Equal(size, result.TotalBytes);
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));

        var segments = (await _server.GetLogAsync(token))
            .Where(IntegrationHelpers.IsSegment)
            .ToList();

        Assert.True(segments.Count >= 3, $"expected several reconnects, saw {segments.Count}");
    }

    [Fact]
    public async Task Unicode_content_disposition_name_is_used()
    {
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size=1000&cd=unicode"));
        using var temp = new TempDirectory();

        var result = await new DownloadEngine().DownloadAsync(
            Request(url, temp.Path, connections: 1),
            IntegrationHelpers.Timeout(30));

        Assert.Equal(temp.FileFor("中文.bin"), result.TargetPath);
        Assert.True(File.Exists(result.TargetPath));
        Assert.Equal(
            await _server.GetChecksumAsync(token, 1000),
            await IntegrationHelpers.Sha256Async(result.TargetPath));
    }

    [Fact]
    public async Task Compressed_responses_are_rejected()
    {
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size=1000&encoding=gzip"));

        await Assert.ThrowsAsync<UnsupportedContentEncodingException>(() =>
            new DownloadEngine().ProbeAsync(url, NoProxy, IntegrationHelpers.Timeout(30)));
    }

    [Fact]
    public async Task Cookie_challenge_is_solved_automatically()
    {
        const long size = 512 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&cookie=required"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("cookie.bin");

        var result = await new DownloadEngine().DownloadAsync(
            Request(url, target, connections: 1),
            IntegrationHelpers.Timeout(60));

        Assert.Equal(size, result.TotalBytes);
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));

        var log = await _server.GetLogAsync(token);
        Assert.Contains(log, entry => entry.Status == 302);
        Assert.Contains(log, entry =>
            entry.Status == 206 &&
            entry.Cookie is not null &&
            entry.Cookie.Contains("session=ok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Per_host_cap_limits_segment_concurrency()
    {
        const long size = 4L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&throttle=2097152"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("capped.bin");

        var request = Request(
            url,
            target,
            connections: 8,
            minSegmentSize: 128 * 1024,
            maxConnectionsPerHost: 2);

        var result = await new DownloadEngine().DownloadAsync(
            request, IntegrationHelpers.Timeout(90));

        Assert.Equal(size, result.TotalBytes);
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));

        var segments = (await _server.GetLogAsync(token))
            .Where(IntegrationHelpers.IsSegment)
            .ToList();

        Assert.Equal(8, segments.Count);
        Assert.Equal(2, IntegrationHelpers.PeakConcurrency(segments));
    }

    [Fact]
    public async Task Cancel_then_resume_continues_from_persisted_offsets()
    {
        const long size = 4L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&throttle=1048576"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("resume.bin");

        var (statePath, state) = await StartAndCancelAsync(url, target);

        Assert.Equal(JobStatus.Paused, state.Status);
        Assert.True(state.BytesDownloaded > 0);
        Assert.True(File.Exists(state.WorkingFile));

        var expectedOffsets = state.Segments
            .Where(segment => segment.BytesDone > 0)
            .Select(segment => segment.Start + segment.BytesDone)
            .ToHashSet();

        Assert.NotEmpty(expectedOffsets);

        var result = await new DownloadEngine().ResumeAsync(
            statePath, Resume(), IntegrationHelpers.Timeout(90));

        Assert.Equal(size, result.TotalBytes);
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));

        var log = await _server.GetLogAsync(token);
        Assert.Contains(log, entry =>
            IntegrationHelpers.IsSegment(entry) &&
            IntegrationHelpers.ParseRangeHeader(entry.Range) is { } range &&
            expectedOffsets.Contains(range.Start));
    }

    [Fact]
    public async Task Changed_resource_is_rejected_and_can_restart()
    {
        const long size = 4L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&throttle=1048576"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("changed.bin");

        var (statePath, _) = await StartAndCancelAsync(url, target);

        await _server.ConfigureAsync(token, version: 2);

        var mismatch = await Assert.ThrowsAsync<ValidatorMismatchException>(() =>
            new DownloadEngine().ResumeAsync(
                statePath, Resume(), IntegrationHelpers.Timeout(60)));

        Assert.Equal(DownloadExitCode.ValidatorMismatch, mismatch.ExitCode);
        Assert.False(File.Exists(target));

        var result = await new DownloadEngine().ResumeAsync(
            statePath, Resume(restartOnChange: true), IntegrationHelpers.Timeout(120));

        Assert.Equal(size, result.TotalBytes);
        Assert.Equal(
            await _server.GetChecksumAsync(token, size, version: 2),
            await IntegrationHelpers.Sha256Async(target));
    }

    [Fact]
    public async Task Externally_truncated_working_file_is_rejected()
    {
        const long size = 4L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&throttle=1048576"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("truncated.bin");

        var (statePath, state) = await StartAndCancelAsync(url, target);

        await using (var stream = new FileStream(
                         state.WorkingFile, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(state.TotalLength / 2);
        }

        await Assert.ThrowsAsync<StateException>(() =>
            new DownloadEngine().ResumeAsync(
                statePath, Resume(), IntegrationHelpers.Timeout(60)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(7L)]
    [InlineData(8L)]
    [InlineData(4095L)]
    [InlineData(4096L)]
    [InlineData(1024L * 1024 - 1)]
    [InlineData(1024L * 1024)]
    [InlineData(1024L * 1024 + 1)]
    public async Task Boundary_sizes_download_exact_bytes(long size)
    {
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("boundary.bin");

        var result = await new DownloadEngine().DownloadAsync(
            Request(url, target, connections: 8, minSegmentSize: 4096),
            IntegrationHelpers.Timeout(60));

        Assert.Equal(size, result.TotalBytes);
        Assert.Equal(size, new FileInfo(target).Length);
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(target));
    }

    [Fact]
    public async Task Short_partial_responses_report_a_transient_failure_after_retries()
    {
        const long size = 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&range=short"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("short.bin");

        var request = Request(
            url, target, connections: 1, minSegmentSize: 1024, maxRetries: 1);

        var error = await Assert.ThrowsAsync<HttpDownloadException>(() =>
            new DownloadEngine().DownloadAsync(request, IntegrationHelpers.Timeout(60)));

        // A short read means the connection ended early, not that the server refused
        // the request, so a caller should retry later rather than give up.
        Assert.True(error.IsTransient);
        Assert.Equal(DownloadExitCode.TransientFailure, error.ExitCode);
        Assert.False(File.Exists(target));

        var state = await new JsonDownloadStateStore()
            .LoadAsync(StatePaths.StateFor(target), default);

        Assert.NotNull(state);
        Assert.Equal(JobStatus.Failed, state!.Status);
    }

    [Fact]
    public async Task Retryable_statuses_report_a_transient_failure_after_retries()
    {
        const long size = 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("unavailable.bin");

        await _server.ConfigureAsync(token, failSegments: 10, failStatus: 503);

        var request = Request(
            url, target, connections: 1, minSegmentSize: size, maxRetries: 1);

        var error = await Assert.ThrowsAsync<HttpDownloadException>(() =>
            new DownloadEngine().DownloadAsync(request, IntegrationHelpers.Timeout(60)));

        Assert.True(error.IsTransient);
        Assert.Equal(DownloadExitCode.TransientFailure, error.ExitCode);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Protocol_violations_keep_the_permanent_failure_code_after_retries()
    {
        // Above MinBytesForSegmented (2 MiB) so the planner really splits; the second
        // segment starts at a nonzero offset, which is what bad-start corrupts.
        const long size = 4 * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&range=bad-start"));
        using var temp = new TempDirectory();
        var target = temp.FileFor("bad-start.bin");

        var request = Request(
            url, target, connections: 2, minSegmentSize: 1024 * 1024, maxRetries: 1);

        var error = await Assert.ThrowsAsync<HttpDownloadException>(() =>
            new DownloadEngine().DownloadAsync(request, IntegrationHelpers.Timeout(60)));

        // The server answers with a Content-Range that starts one byte early. The same
        // request will get the same contradiction, so retrying cannot help.
        Assert.False(error.IsTransient);
        Assert.Equal(DownloadExitCode.HttpPermanentFailure, error.ExitCode);
        Assert.False(File.Exists(target));
    }

    private async Task<(string StatePath, JobState State)> StartAndCancelAsync(
        Uri url,
        string target)
    {
        var engine = new DownloadEngine();
        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(IntegrationHelpers.Timeout(90));

        engine.ProgressChanged += (_, progress) =>
        {
            if (progress.BytesDownloaded >= 64 * 1024)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.DownloadAsync(
                Request(url, target, connections: 4, minSegmentSize: 256 * 1024),
                cancellation.Token));

        var statePath = StatePaths.StateFor(target);
        var state = await new JsonDownloadStateStore().LoadAsync(statePath, default);

        Assert.NotNull(state);
        return (statePath, state!);
    }
}
