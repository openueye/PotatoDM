using PotatoDM.AppCore.Services;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;
using PotatoDM.Core.Transfer;
using PotatoDM.Tests;

namespace PotatoDM.IntegrationTests;

/// <summary>
/// A sidecar that stops advancing looks exactly like a healthy download until the
/// user closes the app and loses the progress. The engine calls the snapshot save
/// "best effort" and used to swallow the failure entirely; these tests pin the
/// Warning that now makes the situation diagnosable.
/// </summary>
public class SnapshotFailureLoggingTests : IClassFixture<TestServerFixture>
{
    private readonly TestServerFixture _server;

    public SnapshotFailureLoggingTests(TestServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public async Task Snapshot_save_failure_is_logged_and_the_transfer_still_finishes()
    {
        const long size = 2L * 1024 * 1024;
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url($"/dl?token={token}&size={size}&throttle={512 * 1024}"));

        using var temp = new TempDirectory();
        var store = new FailAfterFirstSaveStore();
        var log = new RecordingLogSink();
        var engine = new DownloadEngine(store, log: log);

        var result = await engine.DownloadAsync(
            new DownloadRequest
            {
                Url = url,
                TargetPath = temp.FileFor("file.bin"),
                StatePath = temp.FileFor("file.download.json"),
                ProcessDirectory = temp.Path,
                Connections = 2,
                RequestOptions = new RequestOptions { UseProxy = false },
            },
            IntegrationHelpers.Timeout(90));

        Assert.Equal(size, result.TotalBytes);
        Assert.True(store.Saves >= 2, "The snapshot save was never retried, so nothing could fail.");

        var entry = log.Find(LogLevel.Warning, "progress snapshot");

        Assert.True(entry is not null, "No Warning was logged for the failed snapshot: " + log.Transcript);
        Assert.IsType<FileAccessException>(entry!.Exception);
    }

    /// <summary>
    /// Succeeds the first save (the engine requires the initial snapshot) and fails
    /// every later one, which is what a full or read-only disk looks like mid-job.
    /// </summary>
    private sealed class FailAfterFirstSaveStore : IDownloadStateStore
    {
        private readonly JsonDownloadStateStore _inner = new();
        private int _saves;

        public int Saves => Volatile.Read(ref _saves);

        public Task<JobState?> LoadAsync(string statePath, CancellationToken cancellationToken) =>
            _inner.LoadAsync(statePath, cancellationToken);

        public Task SaveAsync(string statePath, JobState state, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _saves) == 1)
            {
                return _inner.SaveAsync(statePath, state, cancellationToken);
            }

            return Task.FromException(
                new FileAccessException("Simulated: the disk refused the snapshot write."));
        }
    }
}
