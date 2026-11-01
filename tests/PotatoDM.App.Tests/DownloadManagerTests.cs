using System.Collections.Concurrent;
using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Media;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;
using PotatoDM.Core.Transfer;

namespace PotatoDM.App.Tests;

public class DownloadManagerTests
{
    [Fact]
    public async Task Add_uses_automatic_connections_from_probe_size()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine
        {
            OnProbe = (url, _) => Task.FromResult(new ProbeResult(
                url, url, 2L * 1024 * 1024 * 1024, true, null, null,
                "large.bin", "application/octet-stream")),
        };
        var settings = new AppSettings { AutomaticConnections = true, Connections = 8 };
        var manager = CreateManager(temp, engine, settings);

        await manager.InitializeAsync();
        await manager.AddAsync(new Uri("https://example.test/large.bin"), temp.Path, 0);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(16, engine.LastDownloadRequest?.Connections);
    }

    [Fact]
    public async Task Add_resolves_the_target_persists_the_job_and_completes()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var engine = new FakeDownloadEngine();
        engine.OnDownload = async (request, cancellationToken) =>
        {
            engine.RaiseProgress(2048, 4096);
            await Task.Yield();
            engine.RaiseProgress(4096, 4096);
            return new DownloadResult(
                request.TargetPath ?? string.Empty,
                4096,
                DownloadMode.Segmented,
                TimeSpan.Zero);
        };

        var manager = CreateManager(temp, engine);
        var job = await manager.AddAsync(
            new Uri("http://example.test/file"), folder, connections: 4);

        Assert.Equal(Path.Combine(folder, "hello", "hello.bin"), job.TargetPath);
        Assert.Equal(Path.Combine(folder, "hello"), job.TaskDirectory);
        Assert.Equal(
            StatePaths.StateIn(
                Path.Combine(folder, "hello", StatePaths.ProcessDirectoryName),
                job.TargetPath),
            job.StatePath);
        Assert.Equal(
            Path.Combine(folder, "hello", StatePaths.ProcessDirectoryName),
            job.ProcessDirectory);

        var registry = await new JobRegistryStore(temp.FileFor("jobs.json")).LoadAsync();
        var record = Assert.Single(registry);
        Assert.Equal(job.Id, record.Id);
        Assert.Equal(job.TargetPath, record.TargetPath);

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(4096, job.BytesDownloaded);
        Assert.Equal(4096, job.TotalBytes);
        Assert.Equal(100, job.PercentValue);
        Assert.Equal(1, engine.ProbeCount);
        Assert.Equal(1, engine.DownloadCount);
    }

    [Fact]
    public async Task Manifest_job_without_length_completes_and_keeps_the_real_target()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var url = new Uri("https://example.test/show/index.m3u8");
        var engine = new FakeDownloadEngine
        {
            OnProbe = (probed, _) => Task.FromResult(new ProbeResult(
                probed,
                probed,
                null,
                false,
                null,
                null,
                "index.m3u8",
                "application/vnd.apple.mpegurl")),
        };

        engine.OnDownload = (request, _) =>
        {
            var taskDirectory = Path.GetDirectoryName(request.TargetPath)!;
            var corrected = Path.Combine(taskDirectory, "index.ts");
            File.WriteAllBytes(corrected, new byte[] { 7, 8, 9 });
            return Task.FromResult(new DownloadResult(
                corrected,
                3,
                DownloadMode.Segmented,
                TimeSpan.Zero));
        };

        var settings = new AppSettings { AutomaticConnections = true, Connections = 5 };
        var manager = CreateManager(temp, engine, settings);
        await manager.InitializeAsync();

        var job = await manager.AddAsync(url, folder, connections: 0);

        Assert.Equal(Path.Combine(folder, "index", "index.mp4"), job.TargetPath);

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(Path.Combine(folder, "index", "index.ts"), job.TargetPath);
        Assert.Equal(5, engine.LastDownloadRequest?.Connections);
        Assert.Equal(3, job.BytesDownloaded);

        var registry = await new JobRegistryStore(temp.FileFor("jobs.json")).LoadAsync();
        Assert.EndsWith("index.ts", Assert.Single(registry).TargetPath);
    }

    [Fact]
    public async Task Pause_cancels_the_engine_and_marks_the_job_paused()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine();
        engine.OnDownload = async (request, cancellationToken) =>
        {
            engine.RaiseProgress(1024, 4096);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new DownloadResult(
                request.TargetPath ?? string.Empty,
                4096,
                DownloadMode.Segmented,
                TimeSpan.Zero);
        };

        var manager = CreateManager(temp, engine);
        var job = await manager.AddAsync(
            new Uri("http://example.test/file"), temp.Path, connections: 4);

        await WaitForAsync(() => job.State == DownloadJobState.Downloading);
        await manager.PauseAsync(job);

        Assert.Equal(DownloadJobState.Paused, job.State);
        Assert.Equal(1024, job.BytesDownloaded);
        Assert.True(job.CanResume);
    }

    [Fact]
    public async Task Initialize_restores_paused_jobs_from_sidecars()
    {
        using var temp = new TempDirectory();
        var target = temp.FileFor("restored.bin");
        var statePath = StatePaths.StateFor(target);
        var now = DateTimeOffset.UtcNow;

        await new JsonDownloadStateStore().SaveAsync(
            statePath,
            new JobState
            {
                Url = "http://example.test/file",
                FinalUrl = "http://example.test/file",
                TotalLength = 1000,
                Mode = DownloadMode.Segmented,
                TargetFile = target,
                WorkingFile = StatePaths.WorkingFor(target),
                Segments = new List<DownloadSegment>
                {
                    new() { Index = 0, Start = 0, EndExclusive = 1000, BytesDone = 400 },
                },
                Status = JobStatus.Paused,
                CreatedUtc = now,
                UpdatedUtc = now,
            },
            CancellationToken.None);

        await SaveRecordAsync(temp, new JobRecord
        {
            Id = "restored",
            Url = "http://example.test/file",
            TargetPath = target,
            StatePath = statePath,
            CreatedUtc = now,
        });

        var manager = CreateManager(temp, new FakeDownloadEngine());
        await manager.InitializeAsync();

        var job = Assert.Single(manager.Jobs);
        Assert.Equal(DownloadJobState.Paused, job.State);
        Assert.Equal(400, job.BytesDownloaded);
        Assert.Equal(1000, job.TotalBytes);
        Assert.Equal(target, job.TargetPath);
        Assert.Equal("restored.bin", job.DisplayName);
    }

    [Fact]
    public async Task Initialize_restores_media_job_from_its_cache_and_restarts_without_a_sidecar()
    {
        using var temp = new TempDirectory();
        var target = temp.FileFor("show.ts");
        Directory.CreateDirectory(target + MediaDownloader.CacheSuffix);

        await SaveRecordAsync(temp, new JobRecord
        {
            Id = "media",
            Url = "https://example.test/show/index.m3u8",
            TargetPath = target,
            StatePath = StatePaths.StateFor(target),
            IsMedia = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        });

        var engine = new FakeDownloadEngine
        {
            OnProbe = (url, _) => Task.FromResult(new ProbeResult(
                url,
                url,
                null,
                false,
                null,
                null,
                "index.m3u8",
                "application/vnd.apple.mpegurl")),
        };

        engine.OnDownload = (request, _) =>
        {
            File.WriteAllBytes(target, new byte[] { 1, 2, 3 });
            return Task.FromResult(new DownloadResult(target, 3, DownloadMode.Segmented, TimeSpan.Zero));
        };

        var manager = CreateManager(temp, engine);
        await manager.InitializeAsync();

        var job = Assert.Single(manager.Jobs);
        Assert.Equal(DownloadJobState.Paused, job.State);
        Assert.True(job.CanResume);
        Assert.Equal(0, engine.DownloadCount);

        await manager.ResumeAsync(job);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, engine.DownloadCount);
        Assert.Equal(0, engine.ResumeCount);
        Assert.Equal(DownloadJobState.Completed, job.State);
    }

    [Fact]
    public async Task Initialize_drops_records_whose_files_are_gone()
    {
        using var temp = new TempDirectory();
        var target = temp.FileFor("gone.bin");

        await SaveRecordAsync(temp, new JobRecord
        {
            Id = "gone",
            Url = "http://example.test/file",
            TargetPath = target,
            StatePath = StatePaths.StateFor(target),
            CreatedUtc = DateTimeOffset.UtcNow,
        });

        var manager = CreateManager(temp, new FakeDownloadEngine());
        await manager.InitializeAsync();

        Assert.Empty(manager.Jobs);
        Assert.Empty(await new JobRegistryStore(temp.FileFor("jobs.json")).LoadAsync());
    }

    [Fact]
    public async Task Resume_starts_the_engine_again_and_completes()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine();
        engine.OnDownload = async (request, cancellationToken) =>
        {
            engine.RaiseProgress(1024, 4096);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new DownloadResult(
                request.TargetPath ?? string.Empty,
                4096,
                DownloadMode.Segmented,
                TimeSpan.Zero);
        };

        var manager = CreateManager(temp, engine);
        var job = await manager.AddAsync(
            new Uri("http://example.test/file"), temp.Path, connections: 4);

        await WaitForAsync(() => job.State == DownloadJobState.Downloading);
        await manager.PauseAsync(job);

        await WriteStateAsync(job, DownloadMode.Segmented, JobStatus.Paused);
        engine.OnResume = (statePath, options, cancellationToken) =>
        {
            engine.RaiseProgress(4096, 4096);
            return Task.FromResult(new DownloadResult(
                job.TargetPath,
                4096,
                DownloadMode.Segmented,
                TimeSpan.Zero));
        };

        Assert.True(job.CanResume);

        await manager.ResumeAsync(job);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, engine.ResumeCount);
        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(4096, job.BytesDownloaded);
    }

    [Fact]
    public async Task Remove_deletes_partial_artifacts_but_keeps_the_finished_file()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine
        {
            OnDownload = (request, cancellationToken) => Task.FromResult(
                new DownloadResult(
                    request.TargetPath ?? string.Empty,
                    4096,
                    DownloadMode.Segmented,
                    TimeSpan.Zero)),
        };

        var manager = CreateManager(temp, engine);
        var job = await manager.AddAsync(
            new Uri("http://example.test/file"), temp.Path, connections: 4);

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadJobState.Completed, job.State);

        await File.WriteAllBytesAsync(job.TargetPath, new byte[4096]);

        var artifacts = new[]
        {
            job.StatePath,
            job.StatePath + ".bak",
            job.StatePath + ".tmp",
            job.StatePath + ".lock",
            StatePaths.WorkingIn(job.ProcessDirectory, job.TargetPath),
        };

        Directory.CreateDirectory(job.ProcessDirectory);

        foreach (var path in artifacts)
        {
            await File.WriteAllTextAsync(path, "stub");
        }

        await manager.RemoveAsync(job, deletePartialData: true);

        Assert.Empty(manager.Jobs);
        Assert.True(File.Exists(job.TargetPath));
        Assert.False(Directory.Exists(job.ProcessDirectory));
        Assert.All(artifacts, path => Assert.False(File.Exists(path)));
        Assert.Empty(await new JobRegistryStore(temp.FileFor("jobs.json")).LoadAsync());
    }

    [Fact]
    public async Task Job_limit_queues_extra_downloads_and_runs_them_in_order()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings
        {
            AutomaticConnections = false,
            Connections = 1,
            MaxConcurrentJobs = 1,
        };
        var gates = CreateGates("one.bin", "two.bin", "three.bin");
        var started = new ConcurrentQueue<string>();
        var sync = new object();
        var running = 0;
        var peak = 0;

        var engine = new FakeDownloadEngine
        {
            OnProbe = (url, _) => Task.FromResult(new ProbeResult(
                url,
                url,
                4096,
                true,
                "\"etag\"",
                null,
                Path.GetFileName(url.AbsolutePath),
                "application/octet-stream")),
            OnDownload = async (request, cancellationToken) =>
            {
                var name = Path.GetFileName(request.TargetPath ?? string.Empty);

                lock (sync)
                {
                    running++;
                    peak = Math.Max(peak, running);
                }

                started.Enqueue(name);
                var gate = gates.GetOrAdd(
                    name,
                    _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

                try
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    lock (sync)
                    {
                        running--;
                    }
                }

                return new DownloadResult(
                    request.TargetPath ?? string.Empty,
                    4096,
                    DownloadMode.Segmented,
                    TimeSpan.Zero);
            },
        };

        var manager = CreateManager(temp, engine, settings);
        await manager.InitializeAsync();

        var first = await manager.AddAsync(
            new Uri("http://example.test/one.bin"), temp.Path, connections: 1);
        var second = await manager.AddAsync(
            new Uri("http://example.test/two.bin"), temp.Path, connections: 1);
        var third = await manager.AddAsync(
            new Uri("http://example.test/three.bin"), temp.Path, connections: 1);

        await WaitForAsync(() => first.State == DownloadJobState.Downloading);
        Assert.Equal(DownloadJobState.Queued, second.State);
        Assert.Equal(DownloadJobState.Queued, third.State);
        Assert.Equal(1, engine.DownloadCount);

        gates["one.bin"].SetResult();
        await WaitForAsync(() => started.Count == 2);

        Assert.Equal(DownloadJobState.Completed, first.State);
        Assert.Equal(DownloadJobState.Downloading, second.State);
        Assert.Equal(DownloadJobState.Queued, third.State);

        gates["two.bin"].SetResult();
        await WaitForAsync(() => started.Count == 3);
        gates["three.bin"].SetResult();

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "one.bin", "two.bin", "three.bin" }, started);
        Assert.Equal(1, peak);
        Assert.Equal(3, engine.DownloadCount);
        Assert.All(manager.Jobs, job => Assert.Equal(DownloadJobState.Completed, job.State));
    }

    [Fact]
    public async Task Raising_the_job_limit_starts_queued_work()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings
        {
            AutomaticConnections = false,
            Connections = 1,
            MaxConcurrentJobs = 1,
        };
        var gates = CreateGates("one.bin", "two.bin");
        var engine = new FakeDownloadEngine
        {
            OnProbe = (url, _) => Task.FromResult(new ProbeResult(
                url,
                url,
                4096,
                true,
                "\"etag\"",
                null,
                Path.GetFileName(url.AbsolutePath),
                "application/octet-stream")),
            OnDownload = async (request, cancellationToken) =>
            {
                var name = Path.GetFileName(request.TargetPath ?? string.Empty);
                await gates[name].Task.WaitAsync(cancellationToken);

                return new DownloadResult(
                    request.TargetPath ?? string.Empty,
                    4096,
                    DownloadMode.Segmented,
                    TimeSpan.Zero);
            },
        };

        var manager = CreateManager(temp, engine, settings);
        await manager.InitializeAsync();

        var first = await manager.AddAsync(
            new Uri("http://example.test/one.bin"), temp.Path, connections: 1);
        var second = await manager.AddAsync(
            new Uri("http://example.test/two.bin"), temp.Path, connections: 1);

        await WaitForAsync(() => first.State == DownloadJobState.Downloading);
        Assert.Equal(DownloadJobState.Queued, second.State);
        Assert.Equal(1, engine.DownloadCount);

        settings.MaxConcurrentJobs = 2;
        manager.RefreshQueue();

        await WaitForAsync(() => engine.DownloadCount == 2);
        Assert.Equal(DownloadJobState.Downloading, second.State);

        gates["one.bin"].SetResult();
        gates["two.bin"].SetResult();
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(10));

        Assert.All(manager.Jobs, job => Assert.Equal(DownloadJobState.Completed, job.State));
    }

    [Fact]
    public async Task Paused_queued_job_starts_over_when_resumed()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings
        {
            AutomaticConnections = false,
            Connections = 1,
            MaxConcurrentJobs = 1,
        };
        var gates = CreateGates("one.bin", "two.bin");
        var engine = new FakeDownloadEngine
        {
            OnProbe = (url, _) => Task.FromResult(new ProbeResult(
                url,
                url,
                4096,
                true,
                "\"etag\"",
                null,
                Path.GetFileName(url.AbsolutePath),
                "application/octet-stream")),
            OnDownload = async (request, cancellationToken) =>
            {
                var name = Path.GetFileName(request.TargetPath ?? string.Empty);
                await gates[name].Task.WaitAsync(cancellationToken);

                return new DownloadResult(
                    request.TargetPath ?? string.Empty,
                    4096,
                    DownloadMode.Segmented,
                    TimeSpan.Zero);
            },
        };

        var manager = CreateManager(temp, engine, settings);
        await manager.InitializeAsync();

        var first = await manager.AddAsync(
            new Uri("http://example.test/one.bin"), temp.Path, connections: 1);
        var second = await manager.AddAsync(
            new Uri("http://example.test/two.bin"), temp.Path, connections: 1);

        await WaitForAsync(() => first.State == DownloadJobState.Downloading);
        Assert.Equal(DownloadJobState.Queued, second.State);

        await manager.PauseAsync(second);

        Assert.Equal(DownloadJobState.Paused, second.State);
        Assert.True(second.CanResume);
        Assert.False(File.Exists(second.StatePath));

        gates["one.bin"].SetResult();
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, engine.DownloadCount);

        await manager.ResumeAsync(second);
        await WaitForAsync(() => engine.DownloadCount == 2);
        Assert.Equal(DownloadJobState.Downloading, second.State);

        gates["two.bin"].SetResult();
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DownloadJobState.Completed, second.State);
    }

    [Fact]
    public async Task Pause_all_also_stops_queued_jobs()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings
        {
            AutomaticConnections = false,
            Connections = 1,
            MaxConcurrentJobs = 1,
        };
        var gates = CreateGates("one.bin", "two.bin");
        var engine = new FakeDownloadEngine
        {
            OnProbe = (url, _) => Task.FromResult(new ProbeResult(
                url,
                url,
                4096,
                true,
                "\"etag\"",
                null,
                Path.GetFileName(url.AbsolutePath),
                "application/octet-stream")),
            OnDownload = async (request, cancellationToken) =>
            {
                var name = Path.GetFileName(request.TargetPath ?? string.Empty);
                await gates[name].Task.WaitAsync(cancellationToken);

                return new DownloadResult(
                    request.TargetPath ?? string.Empty,
                    4096,
                    DownloadMode.Segmented,
                    TimeSpan.Zero);
            },
        };

        var manager = CreateManager(temp, engine, settings);
        await manager.InitializeAsync();

        var first = await manager.AddAsync(
            new Uri("http://example.test/one.bin"), temp.Path, connections: 1);
        var second = await manager.AddAsync(
            new Uri("http://example.test/two.bin"), temp.Path, connections: 1);

        await WaitForAsync(() => first.State == DownloadJobState.Downloading);
        Assert.Equal(DownloadJobState.Queued, second.State);

        await manager.PauseAllAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadJobState.Paused, first.State);
        Assert.Equal(DownloadJobState.Paused, second.State);
        Assert.Equal(1, engine.DownloadCount);
    }

    [Fact]
    public async Task All_jobs_share_one_bandwidth_limiter_that_tracks_settings()
    {
        using var temp = new TempDirectory();
        var requests = new ConcurrentQueue<DownloadRequest>();
        var engine = new FakeDownloadEngine
        {
            OnDownload = (request, _) =>
            {
                requests.Enqueue(request);
                return Task.FromResult(new DownloadResult(
                    request.TargetPath ?? string.Empty,
                    4096,
                    DownloadMode.Segmented,
                    TimeSpan.Zero));
            },
        };

        var settings = new AppSettings();
        var manager = CreateManager(temp, engine, settings);
        await manager.InitializeAsync();

        await manager.AddAsync(new Uri("http://example.test/one.bin"), temp.Path, connections: 1);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));
        await manager.AddAsync(new Uri("http://example.test/two.bin"), temp.Path, connections: 1);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, requests.Count);
        var limiter = Assert.IsType<BandwidthLimiter>(requests.First().BandwidthLimiter);
        Assert.Same(limiter, requests.Last().BandwidthLimiter);
        Assert.Equal(0, limiter.MaxBytesPerSecond);

        settings.MaxBytesPerSecond = 512 * 1024;

        Assert.Equal(512 * 1024, limiter.MaxBytesPerSecond);
    }

    [Fact]
    public async Task Removing_an_unfinished_job_deletes_its_task_folder()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var engine = new FakeDownloadEngine
        {
            OnDownload = async (request, cancellationToken) =>
            {
                Directory.CreateDirectory(request.ProcessDirectory!);
                await File.WriteAllTextAsync(request.StatePath!, "{}", cancellationToken);
                await File.WriteAllTextAsync(
                    StatePaths.WorkingIn(request.ProcessDirectory!, request.TargetPath!),
                    "partial",
                    cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            },
        };

        var manager = CreateManager(temp, engine);
        var job = await manager.AddAsync(
            new Uri("http://example.test/file"), folder, connections: 4);

        await WaitForAsync(() => job.State == DownloadJobState.Downloading);
        Assert.True(Directory.Exists(job.TaskDirectory));

        await manager.RemoveAsync(job, deletePartialData: true);

        Assert.Empty(manager.Jobs);
        Assert.False(Directory.Exists(job.TaskDirectory));
    }

    [Fact]
    public async Task Failed_download_deletes_its_task_folder_and_restarts_from_zero()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var attempts = 0;

        var engine = new FakeDownloadEngine
        {
            OnDownload = async (request, cancellationToken) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    Directory.CreateDirectory(request.ProcessDirectory!);
                    await File.WriteAllTextAsync(request.StatePath!, "{}", cancellationToken);
                    await File.WriteAllTextAsync(
                        StatePaths.WorkingIn(request.ProcessDirectory!, request.TargetPath!),
                        "partial",
                        cancellationToken);
                    throw new IOException("network failed");
                }

                return new DownloadResult(
                    request.TargetPath ?? string.Empty,
                    4096,
                    DownloadMode.Segmented,
                    TimeSpan.Zero);
            },
        };

        var manager = CreateManager(temp, engine);
        var job = await manager.AddAsync(
            new Uri("http://example.test/file"), folder, connections: 4);

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.False(Directory.Exists(job.TaskDirectory));
        Assert.True(job.CanResume);

        await manager.ResumeAsync(job);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, attempts);
        Assert.Equal(DownloadJobState.Completed, job.State);
    }

    private static ConcurrentDictionary<string, TaskCompletionSource> CreateGates(
        params string[] names)
    {
        var gates = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            gates[name] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return gates;
    }

    private static DownloadManager CreateManager(
        TempDirectory temp,
        FakeDownloadEngine engine,
        AppSettings? settings = null)
    {
        return new DownloadManager(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json")),
            new FakeEngineFactory(engine),
            settings ?? new AppSettings(),
            new FakeCookieProtector(),
            new JsonDownloadStateStore());
    }

    private static Task SaveRecordAsync(TempDirectory temp, JobRecord record) =>
        new JobRegistryStore(temp.FileFor("jobs.json")).SaveAsync(new[] { record });

    private static Task WriteStateAsync(
        DownloadJob job,
        DownloadMode mode,
        JobStatus status)
    {
        var now = DateTimeOffset.UtcNow;

        return new JsonDownloadStateStore().SaveAsync(
            job.StatePath,
            new JobState
            {
                Url = job.Url,
                FinalUrl = job.Url,
                TotalLength = 4096,
                Mode = mode,
                TargetFile = job.TargetPath,
                WorkingFile = StatePaths.WorkingFor(job.TargetPath),
                Segments = new List<DownloadSegment>
                {
                    new() { Index = 0, Start = 0, EndExclusive = 4096, BytesDone = 1024 },
                },
                Status = status,
                CreatedUtc = job.CreatedUtc,
                UpdatedUtc = now,
            },
            CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "The expected state was not reached in time.");
    }
}
