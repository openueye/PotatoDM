using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;

namespace PotatoDM.App.Tests;

public class StoreTests
{
    [Fact]
    public async Task Job_registry_round_trips_records()
    {
        using var temp = new TempDirectory();
        var store = new JobRegistryStore(temp.FileFor("jobs.json"));
        var record = new JobRecord
        {
            Id = "abc",
            Url = "http://example.test/file.bin",
            TargetPath = temp.FileFor("file.bin"),
            StatePath = temp.FileFor("file.bin.download.json"),
            CreatedUtc = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
        };

        await store.SaveAsync(new[] { record });
        var loaded = await store.LoadAsync();

        var value = Assert.Single(loaded);
        Assert.Equal(record.Id, value.Id);
        Assert.Equal(record.Url, value.Url);
        Assert.Equal(record.TargetPath, value.TargetPath);
        Assert.Equal(record.StatePath, value.StatePath);
        Assert.Equal(record.CreatedUtc, value.CreatedUtc);
    }

    [Fact]
    public async Task Job_registry_creates_the_directory_and_leaves_no_temp_file()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "nested", "jobs.json");
        var store = new JobRegistryStore(path);

        await store.SaveAsync(new[] { new JobRecord { Id = "a" } });

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Job_registry_returns_empty_for_corrupt_json()
    {
        using var temp = new TempDirectory();
        var path = temp.FileFor("jobs.json");
        await File.WriteAllTextAsync(path, "{ this is not json");

        var loaded = await new JobRegistryStore(path).LoadAsync();

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.FileFor("settings.json"));

        await store.SaveAsync(new AppSettings
        {
            Connections = 16,
            MaxConnectionsPerHost = 4,
            MaxConcurrentJobs = 5,
            MaxBytesPerSecond = 512 * 1024,
            DynamicSegmentation = true,
            SparseWorkingFile = true,
            DownloadFolder = @"C:\Downloads",
            AutomaticConnections = false,
            CloseToTray = false,
            StartMinimized = true,
            LaunchAtStartup = true,
        });

        var loaded = await store.LoadAsync();

        Assert.Equal(16, loaded.Connections);
        Assert.Equal(4, loaded.MaxConnectionsPerHost);
        Assert.Equal(5, loaded.MaxConcurrentJobs);
        Assert.Equal(512 * 1024, loaded.MaxBytesPerSecond);
        Assert.True(loaded.DynamicSegmentation);
        Assert.True(loaded.SparseWorkingFile);
        Assert.Equal(@"C:\Downloads", loaded.DownloadFolder);
        Assert.False(loaded.AutomaticConnections);
        Assert.False(loaded.CloseToTray);
        Assert.True(loaded.StartMinimized);
        Assert.True(loaded.LaunchAtStartup);
    }

    [Fact]
    public async Task Settings_fall_back_to_defaults_when_corrupt()
    {
        using var temp = new TempDirectory();
        var path = temp.FileFor("settings.json");
        await File.WriteAllTextAsync(path, "not json at all");

        var loaded = await new SettingsStore(path).LoadAsync();

        Assert.Equal(8, loaded.Connections);
        Assert.Equal(8, loaded.MaxConnectionsPerHost);
        Assert.Equal(3, loaded.MaxConcurrentJobs);
        Assert.Equal(0, loaded.MaxBytesPerSecond);
        Assert.Null(loaded.DownloadFolder);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(99, 32)]
    public void Settings_normalization_clamps_values(int input, int expected)
    {
        var settings = new AppSettings
        {
            Connections = input,
            MaxConnectionsPerHost = input,
            MaxConcurrentJobs = input,
        };

        settings.Normalize();

        Assert.Equal(expected, settings.Connections);
        Assert.Equal(expected, settings.MaxConnectionsPerHost);
        Assert.Equal(expected, settings.MaxConcurrentJobs);
    }

    [Theory]
    [InlineData(0, true, 1)]
    [InlineData(2 * 1024 * 1024, true, 1)]
    [InlineData(8 * 1024 * 1024, true, 4)]
    [InlineData(64 * 1024 * 1024, true, 8)]
    [InlineData(1024L * 1024 * 1024, true, 16)]
    [InlineData(1024L * 1024 * 1024, false, 1)]
    public void Automatic_connection_policy_uses_safe_size_bands(
        long length,
        bool supportsRanges,
        int expected)
    {
        Assert.Equal(expected, DownloadConnectionPolicy.Select(length, supportsRanges));
    }
}
