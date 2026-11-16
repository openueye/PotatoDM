using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Storage;
using PotatoDM.Torrent;

namespace PotatoDM.App.Tests;

public class TorrentJobTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    private const string Magnet = "magnet:?xt=urn:btih:" + Hash + "&dn=Ubuntu";

    [Fact]
    public async Task Torrent_progress_distinguishes_metadata_and_peer_connection()
    {
        using var temp = new TempDirectory();
        var sendPeers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportedMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportedPeers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = CreateManager(temp, new FakeDownloadEngine(), new AppSettings(), async (request, report, token) =>
        {
            report?.Invoke(new TorrentProgress("test", 0, 0, 0, 0, 0, false));
            reportedMetadata.SetResult();
            await sendPeers.Task.WaitAsync(token);
            report?.Invoke(new TorrentProgress("test", 100, 0, 0, 0, 0, true));
            reportedPeers.SetResult();
            await finish.Task.WaitAsync(token);
            return new TorrentDownloadResult(request.DownloadDirectory, true, 100, "test");
        });
        var job = await manager.AddTorrentAsync(Magnet, temp.Path);
        await reportedMetadata.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("获取元数据", job.StateText);
        sendPeers.SetResult();
        await reportedPeers.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("连接节点", job.StateText);
        Assert.Contains("0 个节点", job.TransferredText);
        finish.SetResult();
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));
        await manager.FlushRegistryAsync();
    }

    [Fact]
    public async Task Torrent_limits_are_passed_to_the_started_task()
    {
        using var temp = new TempDirectory();
        TorrentDownloadRequest? captured = null;
        var settings = new AppSettings { TorrentDownloadBytesPerSecond = 4096, TorrentUploadBytesPerSecond = 2048 };
        var manager = CreateManager(temp, new FakeDownloadEngine(), settings, (request, _, _) =>
        {
            captured = request;
            return Task.FromResult(new TorrentDownloadResult(request.DownloadDirectory, true, 0, "test"));
        });
        await manager.AddTorrentAsync(Magnet, temp.Path);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(captured);
        Assert.Equal(4096, captured.MaximumDownloadBytesPerSecond);
        Assert.Equal(2048, captured.MaximumUploadBytesPerSecond);
        await manager.FlushRegistryAsync();
    }

    [Fact]
    public async Task Browser_magnet_handoff_queues_a_torrent_job_without_probing()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);
        var engine = new FakeDownloadEngine();
        var settings = new AppSettings { DownloadFolder = folder };
        var manager = CreateManager(
            temp,
            engine,
            settings,
            (request, _, _) => Task.FromResult(
                new TorrentDownloadResult(
                    Path.Combine(request.DownloadDirectory, "Ubuntu"),
                    false,
                    2048,
                    "Ubuntu")));

        await manager.InitializeAsync();

        var result = await manager.AddFromBrowserAsync(new BrowserDownloadContext
        {
            Url = new Uri(Magnet),
            Kind = "magnet",
            RequestId = "magnet-1",
        });

        Assert.True(result.Accepted, result.Error);
        Assert.NotNull(result.Job);
        Assert.Equal("Ubuntu", Path.GetFileName(result.Job!.TargetPath));
        Assert.Equal(0, engine.ProbeCount);

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadJobState.Completed, result.Job.State);

        await manager.FlushRegistryAsync();
        var record = Assert.Single(
            await new JobRegistryStore(temp.FileFor("jobs.json")).LoadAsync());
        Assert.True(record.IsTorrent);
        Assert.False(record.IsMedia);
        Assert.Equal(Magnet, record.Url);
    }

    [Fact]
    public async Task Browser_malformed_magnet_is_rejected_before_a_job_is_created()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);
        var engine = new FakeDownloadEngine();
        var manager = CreateManager(
            temp,
            engine,
            new AppSettings { DownloadFolder = folder, MaxConcurrentJobs = 0 });

        await manager.InitializeAsync();

        var result = await manager.AddFromBrowserAsync(new BrowserDownloadContext
        {
            Url = new Uri("magnet:?xt=urn:btih:not-a-hash"),
            Kind = "magnet",
            RequestId = "magnet-bad",
        });

        Assert.False(result.Accepted);
        Assert.Equal("unsupported_torrent", result.Error);
        Assert.Empty(manager.Jobs);
        Assert.Equal(0, engine.ProbeCount);
    }

    [Fact]
    public async Task Initialize_restores_a_torrent_job_as_resumable_without_a_sidecar()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "Ubuntu");
        var registry = new JobRegistryStore(temp.FileFor("jobs.json"));

        await registry.SaveAsync(new[]
        {
            new JobRecord
            {
                Id = "torrent-1",
                Url = Magnet,
                TargetPath = target,
                StatePath = StatePaths.StateFor(target),
                IsTorrent = true,
                CreatedUtc = DateTimeOffset.UtcNow,
            },
        });

        var manager = CreateManager(
            temp,
            new FakeDownloadEngine(),
            new AppSettings { DownloadFolder = folder, MaxConcurrentJobs = 0 });

        await manager.InitializeAsync();

        var job = Assert.Single(manager.Jobs);
        Assert.Equal(DownloadJobState.Paused, job.State);
        Assert.True(job.CanResume);
        Assert.False(job.HasStateFile);
    }

    [Fact]
    public async Task Initialize_marks_a_finished_torrent_job_completed()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "Ubuntu");
        await File.WriteAllBytesAsync(target, new byte[4096]);
        var registry = new JobRegistryStore(temp.FileFor("jobs.json"));

        await registry.SaveAsync(new[]
        {
            new JobRecord
            {
                Id = "torrent-2",
                Url = Magnet,
                TargetPath = target,
                StatePath = StatePaths.StateFor(target),
                IsTorrent = true,
                IsCompleted = true,
                CreatedUtc = DateTimeOffset.UtcNow,
            },
        });

        var manager = CreateManager(
            temp,
            new FakeDownloadEngine(),
            new AppSettings { DownloadFolder = folder, MaxConcurrentJobs = 0 });

        await manager.InitializeAsync();

        var job = Assert.Single(manager.Jobs);
        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(4096, job.TotalBytes);
    }

    [Fact]
    public async Task Initialize_restores_a_preallocated_torrent_as_paused()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "Ubuntu");
        await File.WriteAllBytesAsync(target, new byte[4096]);
        var registry = new JobRegistryStore(temp.FileFor("jobs.json"));

        await registry.SaveAsync(new[]
        {
            new JobRecord
            {
                Id = "torrent-3",
                Url = Magnet,
                TargetPath = target,
                StatePath = StatePaths.StateFor(target),
                IsTorrent = true,
                CreatedUtc = DateTimeOffset.UtcNow,
            },
        });

        var manager = CreateManager(
            temp,
            new FakeDownloadEngine(),
            new AppSettings { DownloadFolder = folder, MaxConcurrentJobs = 0 });

        await manager.InitializeAsync();

        var job = Assert.Single(manager.Jobs);
        Assert.Equal(DownloadJobState.Paused, job.State);
        Assert.True(job.CanResume);
    }

    [Fact]
    public async Task Torrent_progress_is_rendered_as_chunks_with_an_eta()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var manager = CreateManager(
            temp,
            new FakeDownloadEngine(),
            new AppSettings { DownloadFolder = folder },
            async (request, onProgress, token) =>
            {
                onProgress?.Invoke(new TorrentProgress(
                    "Ubuntu",
                    TotalBytes: 1_000_000,
                    BytesDownloaded: 250_000,
                    DownloadBytesPerSecond: 50_000,
                    UploadBytesPerSecond: 0,
                    ConnectedPeers: 4,
                    HasMetadata: true));

                reported.TrySetResult();
                await release.Task.WaitAsync(token);
                return new TorrentDownloadResult(
                    Path.Combine(request.DownloadDirectory, "Ubuntu"),
                    false,
                    1_000_000,
                    "Ubuntu");
            });

        await manager.InitializeAsync();

        var result = await manager.AddFromBrowserAsync(new BrowserDownloadContext
        {
            Url = new Uri(Magnet),
            Kind = "magnet",
            RequestId = "magnet-chunks",
        });

        Assert.True(result.Accepted, result.Error);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var job = result.Job!;
        Assert.True(job.HasSegmentProgress);
        Assert.Equal(48, job.SegmentProgresses.Count);
        Assert.Contains("块", job.SegmentSummary);
        Assert.NotNull(job.Eta);

        release.TrySetResult();
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.All(job.SegmentProgresses, chunk => Assert.True(chunk.IsComplete));

        await manager.FlushRegistryAsync();
        var record = Assert.Single(
            await new JobRegistryStore(temp.FileFor("jobs.json")).LoadAsync());
        Assert.True(record.IsCompleted);
    }

    [Fact]
    public async Task Torrent_checking_progress_is_shown_as_checking()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var manager = CreateManager(
            temp,
            new FakeDownloadEngine(),
            new AppSettings { DownloadFolder = folder },
            async (request, onProgress, token) =>
            {
                onProgress?.Invoke(new TorrentProgress(
                    "Ubuntu",
                    TotalBytes: 1_000_000,
                    BytesDownloaded: 500_000,
                    DownloadBytesPerSecond: 0,
                    UploadBytesPerSecond: 0,
                    ConnectedPeers: 0,
                    HasMetadata: true,
                    IsChecking: true));

                reported.TrySetResult();
                await release.Task.WaitAsync(token);
                return new TorrentDownloadResult(
                    Path.Combine(request.DownloadDirectory, "Ubuntu"),
                    false,
                    1_000_000,
                    "Ubuntu");
            });

        await manager.InitializeAsync();

        var result = await manager.AddFromBrowserAsync(new BrowserDownloadContext
        {
            Url = new Uri(Magnet),
            Kind = "magnet",
            RequestId = "magnet-checking",
        });

        Assert.True(result.Accepted, result.Error);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("校验中", result.Job!.StateText);
        Assert.Equal("—", result.Job.SpeedText);
        Assert.Equal("校验进度 50.0%", result.Job.TransferredText);

        release.TrySetResult();
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));
    }

    private static DownloadManager CreateManager(
        TempDirectory temp,
        FakeDownloadEngine engine,
        AppSettings settings,
        Func<
            TorrentDownloadRequest,
            Action<TorrentProgress>?,
            CancellationToken,
            Task<TorrentDownloadResult>>? torrentDownloader = null) =>
        new(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json")),
            new FakeEngineFactory(engine),
            settings,
            new FakeCookieProtector(),
            new JsonDownloadStateStore(),
            torrentDownloader: torrentDownloader);
}
