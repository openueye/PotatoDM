using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Storage;

namespace PotatoDM.App.Tests;

/// <summary>
/// Cookies that cannot be decrypted used to disappear without a trace: the job was
/// restored, ran unauthenticated, and failed with a 401/403 that looked unrelated
/// to the cookie store.
/// </summary>
public class CookieDecryptLoggingTests
{
    private static readonly Uri Url = new("https://example.test/file.bin");

    [Fact]
    public async Task Undecryptable_cookies_are_logged_at_warning_and_the_job_still_restores()
    {
        using var temp = new TempDirectory();
        var log = new RecordingLogSink();
        var targetPath = temp.FileFor("dl/file.bin");
        var statePath = StatePaths.StateFor(targetPath);

        // A payload on disk keeps the restored row in the list; without it RestoreAsync
        // legitimately drops the record and the job-count assertion would be noise.
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllBytesAsync(targetPath, new byte[16]);

        await new JobRegistryStore(temp.FileFor("jobs.json")).SaveAsync(new[]
        {
            new JobRecord
            {
                Id = "job-1",
                Url = Url.ToString(),
                TargetPath = targetPath,
                StatePath = statePath,
                TaskDirectory = temp.FileFor("dl"),
                CreatedUtc = DateTimeOffset.UtcNow,
                // FakeCookieProtector only understands "protected:" blobs, so this
                // returns null exactly like a real DPAPI failure does.
                ProtectedCookies = "bm90LWRlY3J5cHRhYmxl",
            },
        });

        var manager = new DownloadManager(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json")),
            new FakeEngineFactory(new FakeDownloadEngine()),
            new AppSettings { UseProxy = false },
            new FakeCookieProtector(),
            new JsonDownloadStateStore(),
            () => temp.Path,
            log: log);

        await manager.InitializeAsync();

        Assert.Single(manager.Jobs);
        Assert.True(
            log.Has(LogLevel.Warning, "cookies could not be decrypted"),
            "Nothing was logged when the cookie blob failed to decrypt: " + log.Transcript);
    }

    [Fact]
    public async Task A_job_without_cookies_logs_nothing()
    {
        using var temp = new TempDirectory();
        var log = new RecordingLogSink();
        var targetPath = temp.FileFor("dl/file.bin");

        await new JobRegistryStore(temp.FileFor("jobs.json")).SaveAsync(new[]
        {
            new JobRecord
            {
                Id = "job-2",
                Url = Url.ToString(),
                TargetPath = targetPath,
                StatePath = StatePaths.StateFor(targetPath),
                TaskDirectory = temp.FileFor("dl"),
                CreatedUtc = DateTimeOffset.UtcNow,
                ProtectedCookies = null,
            },
        });

        var manager = new DownloadManager(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json")),
            new FakeEngineFactory(new FakeDownloadEngine()),
            new AppSettings { UseProxy = false },
            new FakeCookieProtector(),
            new JsonDownloadStateStore(),
            () => temp.Path,
            log: log);

        await manager.InitializeAsync();

        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Warning);
    }
}
