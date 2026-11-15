using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Storage;

namespace PotatoDM.App.Tests;

/// <summary>
/// Both of these files used to fail silently: a damaged <c>jobs.json</c> emptied the
/// task list and a damaged <c>settings.json</c> reset every preference, with nothing
/// written anywhere. The next save then overwrote the damaged file, so the data was
/// gone before anyone could look at it.
/// </summary>
public class StoreLoggingTests
{
    [Fact]
    public async Task Corrupt_job_registry_is_logged_at_warning_with_its_path()
    {
        using var temp = new TempDirectory();
        var path = temp.FileFor("jobs.json");
        await File.WriteAllTextAsync(path, "{ this is not json ");

        var log = new RecordingLogSink();
        var store = new JobRegistryStore(path, log);

        var records = await store.LoadAsync();

        Assert.Empty(records);

        var entry = log.Find(LogLevel.Warning, "is not valid JSON");

        Assert.True(entry is not null, "Nothing was logged for the corrupt registry: " + log.Transcript);
        Assert.Contains(path, entry!.Message);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(entry.Exception);
        Assert.NotNull(store.DamagedBackupPath);
        await store.SaveAsync(Array.Empty<JobRecord>());
        Assert.Equal("{ this is not json ", await File.ReadAllTextAsync(store.DamagedBackupPath));
        Assert.Empty(await new JobRegistryStore(path).LoadAsync());
    }

    [Fact]
    public async Task A_registry_that_cannot_be_moved_aside_still_starts_empty()
    {
        using var temp = new TempDirectory();
        var path = temp.FileFor("jobs.json");
        await File.WriteAllTextAsync(path, "{ this is not json ");

        // Readers are allowed, but delete/rename is not, so the store can read
        // the damaged file and then fail to preserve it.
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var log = new RecordingLogSink();
        var store = new JobRegistryStore(path, log);

        Assert.Empty(await store.LoadAsync());

        Assert.Null(store.DamagedBackupPath);
        Assert.True(store.DamagedRegistryUnpreserved);
        Assert.True(
            log.Has(LogLevel.Error, "could not be preserved"),
            "The unpreserved registry was not logged: " + log.Transcript);
        Assert.Equal("{ this is not json ", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_registry_holding_a_bare_null_is_logged_too()
    {
        using var temp = new TempDirectory();
        var path = temp.FileFor("jobs.json");
        await File.WriteAllTextAsync(path, "null");

        var log = new RecordingLogSink();
        var store = new JobRegistryStore(path, log);

        Assert.Empty(await store.LoadAsync());

        Assert.True(
            log.Has(LogLevel.Warning, "contains no list"),
            "A registry containing literal null was accepted silently: " + log.Transcript);
    }

    [Fact]
    public async Task A_healthy_registry_logs_nothing()
    {
        using var temp = new TempDirectory();
        var path = temp.FileFor("jobs.json");
        var store = new JobRegistryStore(path);

        await store.SaveAsync(new[]
        {
            new JobRecord
            {
                Id = "job-1",
                Url = "https://example.test/file.bin",
                TargetPath = temp.FileFor("dl/file.bin"),
                StatePath = temp.FileFor("dl/state.json"),
                CreatedUtc = DateTimeOffset.UtcNow,
            },
        });

        var log = new RecordingLogSink();
        var records = await new JobRegistryStore(path, log).LoadAsync();

        Assert.Single(records);
        Assert.DoesNotContain(log.Entries, item => item.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Corrupt_settings_are_logged_at_warning_with_their_path()
    {
        using var temp = new TempDirectory();
        var path = temp.FileFor("settings.json");
        await File.WriteAllTextAsync(path, "{ \"connections\": ");

        var log = new RecordingLogSink();
        var settings = await new SettingsStore(path, log).LoadAsync();

        Assert.Equal(8, settings.Connections);

        var entry = log.Find(LogLevel.Warning, "falls back");

        Assert.True(entry is not null, "Nothing was logged for the corrupt settings: " + log.Transcript);
        Assert.Contains(path, entry!.Message);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(entry.Exception);
    }

    [Fact]
    public async Task Healthy_settings_log_nothing()
    {
        using var temp = new TempDirectory();
        var path = temp.FileFor("settings.json");
        await new SettingsStore(path).SaveAsync(new AppSettings { Connections = 4 });

        var log = new RecordingLogSink();
        var settings = await new SettingsStore(path, log).LoadAsync();

        Assert.Equal(4, settings.Connections);
        Assert.DoesNotContain(log.Entries, item => item.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task The_manager_reads_the_registry_through_the_injected_sink()
    {
        // Wiring proof: the store logs only if the manager actually passes its own
        // sink down when it builds the default registry.
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(temp.FileFor("jobs.json"), "not json at all");

        var log = new RecordingLogSink();
        var manager = new DownloadManager(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json"), log),
            new FakeEngineFactory(new FakeDownloadEngine()),
            new AppSettings { UseProxy = false },
            new FakeCookieProtector(),
            new JsonDownloadStateStore(),
            () => temp.Path,
            log: log);

        await manager.InitializeAsync();

        Assert.Empty(manager.Jobs);
        Assert.True(
            log.Has(LogLevel.Warning, "not valid JSON"),
            "InitializeAsync swallowed the corrupt registry: " + log.Transcript);
    }
}
