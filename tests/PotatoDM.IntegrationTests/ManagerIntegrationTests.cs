using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Storage;

namespace PotatoDM.IntegrationTests;

[Collection(TestServerCollection.Name)]
public class ManagerIntegrationTests
{
    private readonly TestServerFixture _server;

    public ManagerIntegrationTests(TestServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public async Task Manager_downloads_a_file_through_the_engine()
    {
        const long size = 4L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(
            _server.Url($"/dl?token={token}&size={size}&throttle=2097152&cd=ascii"));
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var manager = CreateManager(temp);

        var job = await manager.AddAsync(url, folder, connections: 4);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(90));

        Assert.True(
            job.State == DownloadJobState.Completed,
            $"job ended as {job.State}: {job.Error}");

        Assert.Equal(size, job.BytesDownloaded);
        Assert.Equal(size, job.TotalBytes);
        Assert.Equal(Path.Combine(folder, "hello", "hello.bin"), job.TargetPath);
        Assert.True(File.Exists(Path.Combine(folder, "hello", StatePaths.ProcessDirectoryName,
            "hello.bin" + StatePaths.StateSuffix)));
        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(job.TargetPath));
    }

    [Fact]
    public async Task Manager_pauses_restores_and_resumes_a_job()
    {
        const long size = 4L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(
            _server.Url($"/dl?token={token}&size={size}&throttle=1048576&cd=ascii"));
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var manager = CreateManager(temp);

        var job = await manager.AddAsync(url, folder, connections: 4);

        var progressed = await IntegrationHelpers.WaitUntilAsync(
            () => Task.FromResult(
                job.State == DownloadJobState.Downloading &&
                job.BytesDownloaded >= 256 * 1024),
            TimeSpan.FromSeconds(60));

        Assert.True(progressed, $"no progress. state={job.State}, error={job.Error}");

        await manager.PauseAsync(job);
        Assert.Equal(DownloadJobState.Paused, job.State);

        var restored = CreateManager(temp);
        await restored.InitializeAsync();

        var restoredJob = Assert.Single(restored.Jobs);
        Assert.Equal(DownloadJobState.Paused, restoredJob.State);
        Assert.True(restoredJob.BytesDownloaded > 0);
        Assert.Equal(job.TargetPath, restoredJob.TargetPath);

        await restored.ResumeAsync(restoredJob);
        await restored.WaitForIdleAsync(TimeSpan.FromSeconds(90));

        Assert.True(
            restoredJob.State == DownloadJobState.Completed,
            $"resume ended as {restoredJob.State}: {restoredJob.Error}");

        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(restoredJob.TargetPath));
    }

    [Fact]
    public async Task Browser_handoff_sends_cookies_and_can_resume_after_restart()
    {
        const long size = 4L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url(
            $"/dl?token={token}&size={size}&throttle=1048576&cd=ascii" +
            "&cookie=strict&cookieValue=letmein"));
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var manager = CreateBrowserManager(temp, folder);

        var result = await manager.AddFromBrowserAsync(new BrowserDownloadContext
        {
            Url = url,
            RequestId = "browser-" + Guid.NewGuid().ToString("N"),
            SuggestedFileName = "browser.bin",
            Referer = _server.Url("/page"),
            UserAgent = "IntegrationBrowser/1.0",
            Cookies = new[]
            {
                new PotatoDM.Core.Models.RequestCookie
                {
                    Name = "session",
                    Value = "letmein",
                    Domain = "127.0.0.1",
                    Path = "/",
                },
            },
        });

        Assert.True(result.Accepted, result.Error + ": " + result.Message);
        Assert.NotNull(result.Job);
        Assert.Equal(Path.Combine(folder, "hello", "hello.bin"), result.Job!.TargetPath);

        var progressed = await IntegrationHelpers.WaitUntilAsync(
            () => Task.FromResult(
                result.Job.State == DownloadJobState.Downloading &&
                result.Job.BytesDownloaded >= 256 * 1024),
            TimeSpan.FromSeconds(60));
        Assert.True(progressed, $"no progress. state={result.Job.State}, error={result.Job.Error}");

        await manager.PauseAsync(result.Job);
        Assert.Equal(DownloadJobState.Paused, result.Job.State);

        var record = Assert.Single(
            await new JobRegistryStore(temp.FileFor("jobs.json")).LoadAsync());
        Assert.NotNull(record.ProtectedCookies);
        Assert.Equal("IntegrationBrowser/1.0", record.UserAgent);

        var restored = CreateBrowserManager(temp, folder);
        await restored.InitializeAsync();

        var restoredJob = Assert.Single(restored.Jobs);
        Assert.Equal(DownloadJobState.Paused, restoredJob.State);

        await restored.ResumeAsync(restoredJob);
        await restored.WaitForIdleAsync(TimeSpan.FromSeconds(90));

        Assert.True(
            restoredJob.State == DownloadJobState.Completed,
            $"resume ended as {restoredJob.State}: {restoredJob.Error}");

        Assert.Equal(
            await _server.GetChecksumAsync(token, size),
            await IntegrationHelpers.Sha256Async(restoredJob.TargetPath));

        var log = await _server.GetLogAsync(token);
        Assert.NotEmpty(log);
        Assert.All(log, entry => Assert.Contains("session=letmein", entry.Cookie ?? string.Empty));
    }

    [Fact]
    public async Task Browser_handoff_rejects_chunked_responses()
    {
        var token = IntegrationHelpers.NewToken();
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var manager = CreateBrowserManager(temp, folder);

        var result = await manager.AddFromBrowserAsync(new BrowserDownloadContext
        {
            Url = new Uri(_server.Url($"/chunked?token={token}")),
            RequestId = "chunked-" + Guid.NewGuid().ToString("N"),
        });

        Assert.False(result.Accepted);
        Assert.Equal("no_length", result.Error);
        Assert.Empty(manager.Jobs);
        Assert.Empty(await new JobRegistryStore(temp.FileFor("jobs.json")).LoadAsync());
    }

    [Fact]
    public async Task Restart_after_a_real_failure_reuses_the_folder_and_clears_the_saved_job()
    {
        const long size = 2L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&throttle=8388608"));
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var manager = CreateManager(temp);

        // A 404 on the first segment request is not worth retrying, so the job
        // fails mid-transfer after the sidecar and working file were written.
        await _server.ConfigureAsync(token, failSegments: 1, failStatus: 404);

        var job = await manager.AddAsync(url, folder, connections: 4);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(90));

        Assert.True(
            job.State == DownloadJobState.Failed,
            $"job ended as {job.State}: {job.Error}");
        Assert.True(File.Exists(job.StatePath), "the sidecar must survive the failure");

        var taskDirectory = job.TaskDirectory;

        await _server.ConfigureAsync(token, failSegments: 0);

        // The real engine deletes the stale sidecar inside its state lock. If
        // Restart were not threaded through, it would throw
        // StateException("A saved job already exists") instead.
        await manager.RestartAsync(job);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(90));

        Assert.True(
            job.State == DownloadJobState.Completed,
            $"restart ended as {job.State}: {job.Error}");
        Assert.Equal(taskDirectory, job.TaskDirectory);
        Assert.Single(Directory.GetDirectories(folder));
    }

    private static DownloadManager CreateManager(TempDirectory temp)
    {
        return new DownloadManager(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json")),
            new DownloadEngineFactory(),
            new AppSettings { Connections = 4, UseProxy = false },
            new PassthroughCookieProtector(),
            new JsonDownloadStateStore());
    }

    private static DownloadManager CreateBrowserManager(TempDirectory temp, string folder)
    {
        Directory.CreateDirectory(folder);

        return new DownloadManager(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json")),
            new DownloadEngineFactory(),
            new AppSettings
            {
                Connections = 4,
                UseProxy = false,
                DownloadFolder = folder,
            },
            new PassthroughCookieProtector(),
            new JsonDownloadStateStore());
    }
}
