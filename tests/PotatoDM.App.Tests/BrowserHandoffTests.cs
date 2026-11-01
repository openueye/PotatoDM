using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;
using PotatoDM.Core.Transfer;

namespace PotatoDM.App.Tests;

public class BrowserHandoffTests
{
    [Fact]
    public async Task Browser_add_persists_metadata_and_resume_reuses_it()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);

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

        var settings = new AppSettings { DownloadFolder = folder };
        var manager = CreateManager(temp, engine, settings);
        var context = new BrowserDownloadContext
        {
            Url = new Uri("http://example.test/file"),
            RequestId = "handoff-1",
            SuggestedFileName = "browser-name.bin",
            Referer = "http://example.test/page",
            UserAgent = "Browser/1.0",
            Cookies = new[]
            {
                new RequestCookie
                {
                    Name = "session",
                    Value = "secret-value",
                    Domain = "example.test",
                    Path = "/",
                    HttpOnly = true,
                },
            },
            ExtraHeaders = new Dictionary<string, string>
            {
                ["Accept-Language"] = "zh-CN",
            },
        };

        var result = await manager.AddFromBrowserAsync(context);

        Assert.True(result.Accepted);
        Assert.NotNull(result.Job);
        Assert.Equal(Path.Combine(folder, "hello", "hello.bin"), result.Job!.TargetPath);

        var probeOptions = Assert.IsType<RequestOptions>(engine.LastProbeOptions);
        Assert.Equal("http://example.test/page", probeOptions.Referer);
        Assert.Equal("Browser/1.0", probeOptions.UserAgent);
        var cookie = Assert.Single(probeOptions.Cookies!);
        Assert.Equal("session", cookie.Name);
        Assert.Equal("secret-value", cookie.Value);

        await WaitForAsync(() => result.Job.State == DownloadJobState.Downloading);
        await manager.PauseAsync(result.Job);
        await WriteStateAsync(result.Job);

        var record = Assert.Single(
            await new JobRegistryStore(temp.FileFor("jobs.json")).LoadAsync());
        Assert.Equal("http://example.test/page", record.Referer);
        Assert.Equal("Browser/1.0", record.UserAgent);
        Assert.Equal("zh-CN", record.ExtraHeaders!["Accept-Language"]);
        Assert.NotNull(record.ProtectedCookies);
        Assert.StartsWith("protected:", record.ProtectedCookies, StringComparison.Ordinal);

        var resumeEngine = new FakeDownloadEngine();
        ResumeOptions? resumeOptions = null;
        resumeEngine.OnResume = (statePath, options, cancellationToken) =>
        {
            resumeOptions = options;
            return Task.FromResult(new DownloadResult(
                result.Job!.TargetPath,
                4096,
                DownloadMode.Segmented,
                TimeSpan.Zero));
        };

        var restored = CreateManager(temp, resumeEngine);
        await restored.InitializeAsync();

        var job = Assert.Single(restored.Jobs);
        Assert.Equal(DownloadJobState.Paused, job.State);

        await restored.ResumeAsync(job);
        await restored.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(resumeOptions);
        Assert.Equal("http://example.test/page", resumeOptions!.RequestOptions.Referer);
        Assert.Equal("Browser/1.0", resumeOptions.RequestOptions.UserAgent);
        var resumedCookie = Assert.Single(resumeOptions.RequestOptions.Cookies!);
        Assert.Equal("secret-value", resumedCookie.Value);
        Assert.Equal("zh-CN", resumeOptions.RequestOptions.ExtraHeaders!["Accept-Language"]);
    }

    [Fact]
    public async Task Browser_add_rejects_sizes_it_cannot_segment_and_leaves_no_row()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);

        var engine = new FakeDownloadEngine
        {
            OnProbe = (url, _) => Task.FromResult(new ProbeResult(
                url,
                url,
                null,
                false,
                null,
                null,
                "chunked.bin",
                "application/octet-stream")),
        };

        var manager = CreateManager(temp, engine, new AppSettings { DownloadFolder = folder });
        var result = await manager.AddFromBrowserAsync(BrowserContext());

        Assert.False(result.Accepted);
        Assert.Equal("no_length", result.Error);
        Assert.Empty(manager.Jobs);
        Assert.Empty(await new JobRegistryStore(temp.FileFor("jobs.json")).LoadAsync());
        Assert.Equal(0, engine.DownloadCount);
    }

    [Fact]
    public async Task Browser_add_rejects_probe_timeouts_and_leaves_no_row()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);

        var engine = new FakeDownloadEngine
        {
            OnProbe = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            },
        };

        var manager = CreateManager(
            temp,
            engine,
            new AppSettings { DownloadFolder = folder },
            browserProbeTimeout: TimeSpan.FromMilliseconds(150));

        var result = await manager.AddFromBrowserAsync(BrowserContext());

        Assert.False(result.Accepted);
        Assert.Equal("probe_timeout", result.Error);
        Assert.Empty(manager.Jobs);
        Assert.Equal(0, engine.DownloadCount);
    }

    [Fact]
    public async Task Browser_add_returns_the_same_result_for_a_repeated_request_id()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);

        var engine = new FakeDownloadEngine();
        var manager = CreateManager(temp, engine, new AppSettings { DownloadFolder = folder });
        var context = BrowserContext();

        var first = await manager.AddFromBrowserAsync(context);
        var second = await manager.AddFromBrowserAsync(context);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal(1, engine.ProbeCount);
        Assert.Single(manager.Jobs);
    }

    [Fact]
    public async Task Browser_add_uses_the_default_folder_when_last_folder_is_gone()
    {
        using var temp = new TempDirectory();
        var fallback = Path.Combine(temp.Path, "fallback");
        var settings = new AppSettings
        {
            DownloadFolder = Path.Combine(temp.Path, "does-not-exist"),
        };

        var manager = CreateManager(
            temp,
            new FakeDownloadEngine(),
            settings,
            defaultFolder: () => fallback);

        var result = await manager.AddFromBrowserAsync(BrowserContext());

        Assert.True(result.Accepted);
        Assert.Equal(Path.Combine(fallback, "hello", "hello.bin"), result.TargetPath);
    }

    [Fact]
    public async Task Browser_add_avoids_an_existing_task_folder()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(Path.Combine(folder, "report"));
        await File.WriteAllTextAsync(
            Path.Combine(folder, "report", "report.bin") + StatePaths.StateSuffix,
            "{}");

        var engine = new FakeDownloadEngine
        {
            OnProbe = (url, _) => Task.FromResult(new ProbeResult(
                url,
                url,
                4096,
                true,
                "\"etag\"",
                null,
                "report.bin",
                "application/octet-stream")),
        };

        var manager = CreateManager(temp, engine, new AppSettings { DownloadFolder = folder });
        var result = await manager.AddFromBrowserAsync(BrowserContext());

        Assert.True(result.Accepted);
        Assert.Equal(Path.Combine(folder, "report (1)", "report (1).bin"), result.TargetPath);
    }

    [Fact]
    public async Task Browser_add_prefers_the_browser_name_when_probe_has_no_real_name()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);

        var engine = new FakeDownloadEngine
        {
            OnProbe = (url, _) => Task.FromResult(new ProbeResult(
                url,
                url,
                4096,
                true,
                "\"etag\"",
                null,
                "download.bin",
                "application/octet-stream")),
        };

        var manager = CreateManager(temp, engine, new AppSettings { DownloadFolder = folder });
        var result = await manager.AddFromBrowserAsync(new BrowserDownloadContext
        {
            Url = new Uri("http://example.test/file"),
            SuggestedFileName = "report.pdf",
        });

        Assert.True(result.Accepted);
        Assert.Equal(Path.Combine(folder, "report", "report.pdf"), result.TargetPath);
    }

    [Fact]
    public async Task Removing_a_job_while_it_is_probing_rejects_the_handoff()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        Directory.CreateDirectory(folder);

        var engine = new FakeDownloadEngine
        {
            OnProbe = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            },
        };

        var manager = CreateManager(temp, engine, new AppSettings { DownloadFolder = folder });

        var pending = manager.AddFromBrowserAsync(BrowserContext());
        await WaitForAsync(() => manager.Jobs.Count == 1);

        await manager.RemoveAsync(manager.Jobs[0], deletePartialData: false);

        var result = await pending;
        Assert.False(result.Accepted);
        Assert.Equal("cancelled", result.Error);
        Assert.Empty(manager.Jobs);
        Assert.Equal(0, engine.DownloadCount);
    }

    [Fact]
    public async Task Browser_add_rejects_non_http_schemes()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine();
        var manager = CreateManager(temp, engine);

        var result = await manager.AddFromBrowserAsync(new BrowserDownloadContext
        {
            Url = new Uri("ftp://example.test/file"),
        });

        Assert.False(result.Accepted);
        Assert.Equal("invalid_request", result.Error);
        Assert.Equal(0, engine.ProbeCount);
        Assert.Empty(manager.Jobs);
    }

    private static BrowserDownloadContext BrowserContext() => new()
    {
        Url = new Uri("http://example.test/file"),
        RequestId = "handoff-" + Guid.NewGuid().ToString("N"),
        SuggestedFileName = "browser-name.bin",
    };

    private static DownloadManager CreateManager(
        TempDirectory temp,
        FakeDownloadEngine engine,
        AppSettings? settings = null,
        Func<string>? defaultFolder = null,
        TimeSpan? browserProbeTimeout = null)
    {
        return new DownloadManager(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json")),
            new FakeEngineFactory(engine),
            settings ?? new AppSettings(),
            new FakeCookieProtector(),
            new JsonDownloadStateStore(),
            defaultFolder,
            browserProbeTimeout);
    }

    private static Task WriteStateAsync(DownloadJob job)
    {
        var now = DateTimeOffset.UtcNow;

        return new JsonDownloadStateStore().SaveAsync(
            job.StatePath,
            new JobState
            {
                Url = job.Url,
                FinalUrl = job.Url,
                TotalLength = 4096,
                Mode = DownloadMode.Segmented,
                TargetFile = job.TargetPath,
                WorkingFile = StatePaths.WorkingFor(job.TargetPath),
                Segments = new List<DownloadSegment>
                {
                    new() { Index = 0, Start = 0, EndExclusive = 4096, BytesDone = 1024 },
                },
                Status = JobStatus.Paused,
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
