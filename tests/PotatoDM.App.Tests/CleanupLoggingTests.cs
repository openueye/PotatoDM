using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Storage;

namespace PotatoDM.App.Tests;

/// <summary>
/// "产物清理失败" was the loudest silent failure in the app: the retry loop gave up
/// and returned, so a task folder that could not be deleted simply stayed on disk
/// with no record anywhere. These tests pin the log entry that replaces the
/// silence.
/// </summary>
public class CleanupLoggingTests
{
    private static readonly Uri Url = new("https://example.test/file.bin");

    [Fact]
    public async Task Task_folder_that_cannot_be_deleted_is_logged_at_warning()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine();
        var log = new RecordingLogSink();
        var manager = CreateManager(temp, engine, log);

        var job = await manager.AddAsync(Url, temp.FileFor("dl"), connections: 2);
        Assert.False(string.IsNullOrEmpty(job.TaskDirectory));

        // Hold a handle that refuses sharing inside the job's process folder. Every
        // retry of Directory.Delete then fails with IOException, including the last
        // one — which is exactly the branch that used to return silently.
        var processDirectory = Path.Combine(job.TaskDirectory!, StatePaths.ProcessDirectoryName);
        Directory.CreateDirectory(processDirectory);

        var blockerPath = Path.Combine(processDirectory, "blocker.bin");
        await File.WriteAllBytesAsync(blockerPath, new byte[64]);

        var blocker = new FileStream(
            blockerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        try
        {
            await manager.RemoveAsync(job, deletePartialData: true).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await blocker.DisposeAsync();
        }

        var entry = log.Find(LogLevel.Warning, "could not be deleted after 5 attempts");

        Assert.True(entry is not null, "No Warning was logged for the failed delete: " + log.Transcript);
        Assert.Contains(job.TaskDirectory!, entry!.Message);
        Assert.NotNull(entry.Exception);
    }

    [Fact]
    public async Task Successful_cleanup_logs_nothing_at_warning_or_above()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine();
        var log = new RecordingLogSink();
        var manager = CreateManager(temp, engine, log);

        var job = await manager.AddAsync(Url, temp.FileFor("dl"), connections: 2);
        await manager.RemoveAsync(job, deletePartialData: true).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(
            log.Entries,
            item => item.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Failed_delete_reaches_the_log_file_on_disk()
    {
        using var temp = new TempDirectory();

        // End to end: the real file sink, not just the recording double, so this
        // proves the entry a user would actually find in %LOCALAPPDATA%.
        var logDirectory = temp.FileFor("logs");
        var sink = new FileLogSink(logDirectory);
        var manager = CreateManager(temp, new FakeDownloadEngine(), sink);

        var job = await manager.AddAsync(Url, temp.FileFor("dl"), connections: 2);
        var processDirectory = Path.Combine(job.TaskDirectory!, StatePaths.ProcessDirectoryName);
        Directory.CreateDirectory(processDirectory);
        var blockerPath = Path.Combine(processDirectory, "blocker.bin");
        await File.WriteAllBytesAsync(blockerPath, new byte[64]);

        await using (new FileStream(blockerPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await manager.RemoveAsync(job, deletePartialData: true).WaitAsync(TimeSpan.FromSeconds(30));
        }

        var logFile = Path.Combine(logDirectory, $"potatodm-{DateTimeOffset.Now:yyyyMMdd}.log");
        Assert.True(File.Exists(logFile), $"No log file was written at '{logFile}'.");

        var text = await File.ReadAllTextAsync(logFile);
        Assert.Contains("[WARNING]", text);
        Assert.Contains("could not be deleted after 5 attempts", text);
    }

    private static DownloadManager CreateManager(
        TempDirectory temp,
        FakeDownloadEngine engine,
        ILogSink log)
    {
        return new DownloadManager(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json")),
            new FakeEngineFactory(engine),
            new AppSettings { Connections = 2, UseProxy = false },
            new FakeCookieProtector(),
            new JsonDownloadStateStore(),
            () => temp.FileFor("default"),
            log: log);
    }
}
