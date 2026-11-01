using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;

namespace PotatoDM.App.Tests;

/// <summary>
/// A transfer that dies at 90% must not cost the user the 90% they already paid
/// for. These tests pin the two ways out of a failed job: resume from the
/// surviving sidecar, or restart from zero in the same task folder.
/// </summary>
public class FailureRecoveryTests
{
    [Fact]
    public async Task Failed_download_resumes_from_its_sidecar_instead_of_starting_over()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");

        var engine = new FakeDownloadEngine
        {
            // A second fresh download means the manager started over, which is the
            // regression this test exists to catch.
            OnDownload = async (request, cancellationToken) =>
            {
                Directory.CreateDirectory(request.ProcessDirectory!);
                await File.WriteAllTextAsync(request.StatePath!, "{}", cancellationToken);
                await File.WriteAllTextAsync(
                    StatePaths.WorkingIn(request.ProcessDirectory!, request.TargetPath!),
                    "partial",
                    cancellationToken);
                throw new IOException("network failed");
            },
        };

        var manager = DownloadManagerTests.CreateManager(temp, engine);
        var job = await manager.AddAsync(
            new Uri("http://example.test/file"), folder, connections: 4);

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadJobState.Failed, job.State);

        await manager.ResumeAsync(job);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, engine.ResumeCount);
        Assert.Equal(1, engine.DownloadCount);
        Assert.Equal(DownloadJobState.Completed, job.State);
    }

    [Fact]
    public async Task Restart_downloads_again_in_the_same_folder_without_a_numbered_copy()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var attempts = 0;
        var restarts = new List<bool>();

        var engine = new FakeDownloadEngine
        {
            OnDownload = async (request, cancellationToken) =>
            {
                lock (restarts)
                {
                    restarts.Add(request.Restart);
                }

                if (Interlocked.Increment(ref attempts) == 1)
                {
                    Directory.CreateDirectory(request.ProcessDirectory!);
                    await File.WriteAllTextAsync(request.StatePath!, "{}", cancellationToken);
                    throw new IOException("network failed");
                }

                return new DownloadResult(
                    request.TargetPath ?? string.Empty,
                    4096,
                    DownloadMode.Segmented,
                    TimeSpan.Zero);
            },
        };

        var manager = DownloadManagerTests.CreateManager(temp, engine);
        var job = await manager.AddAsync(
            new Uri("http://example.test/file"), folder, connections: 4);

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.True(job.CanRestart);

        var taskDirectory = job.TaskDirectory;

        await manager.RestartAsync(job);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadJobState.Completed, job.State);

        // Same folder, no "hello (1)" sibling: restart is not Remove+Add.
        Assert.Equal(taskDirectory, job.TaskDirectory);
        Assert.Single(Directory.GetDirectories(folder));
        Assert.Single(manager.Jobs);
        Assert.Equal(new[] { false, true }, restarts);
    }

    /// <summary>
    /// DownloadEngine refuses a fresh request over an existing sidecar
    /// (<c>StateException: A saved job already exists</c>) unless the request
    /// carries <c>Restart</c>. The fake reproduces that guard, so this test fails
    /// if Restart stops being threaded through PendingStart into DownloadRequest.
    /// </summary>
    [Fact]
    public async Task Restart_carries_the_flag_past_the_engines_saved_job_guard()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");
        var attempts = 0;

        var engine = new FakeDownloadEngine();
        engine.OnDownload = async (request, cancellationToken) =>
        {
            var isFirstAttempt = Interlocked.Increment(ref attempts) == 1;

            if (!request.Restart &&
                request.StatePath is { } statePath &&
                File.Exists(statePath))
            {
                throw new StateException(
                    $"A saved job already exists at '{statePath}'. Resume it, or restart with --restart.");
            }

            if (isFirstAttempt)
            {
                Directory.CreateDirectory(request.ProcessDirectory!);
                await File.WriteAllTextAsync(request.StatePath!, "{}", cancellationToken);
                throw new IOException("network failed");
            }

            return new DownloadResult(
                request.TargetPath ?? string.Empty,
                4096,
                DownloadMode.Segmented,
                TimeSpan.Zero);
        };

        var manager = DownloadManagerTests.CreateManager(temp, engine);
        var job = await manager.AddAsync(
            new Uri("http://example.test/file"), folder, connections: 4);

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.True(File.Exists(job.StatePath));

        await manager.RestartAsync(job);
        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        Assert.True(
            job.State == DownloadJobState.Completed,
            $"restart ended as {job.State}: {job.Error}");
    }

    [Theory]
    [InlineData(DownloadJobState.Completed, false)]
    [InlineData(DownloadJobState.Downloading, false)]
    [InlineData(DownloadJobState.Queued, false)]
    [InlineData(DownloadJobState.Paused, true)]
    [InlineData(DownloadJobState.Failed, true)]
    public void CanRestart_covers_only_the_retryable_states(DownloadJobState state, bool expected)
    {
        var job = new DownloadJob("id", "http://example.test/file", DateTimeOffset.UtcNow);

        switch (state)
        {
            case DownloadJobState.Completed:
                job.MarkCompleted(4096);
                break;
            case DownloadJobState.Downloading:
                job.MarkDownloading();
                break;
            case DownloadJobState.Queued:
                job.MarkQueued();
                break;
            case DownloadJobState.Paused:
                job.MarkPaused();
                break;
            case DownloadJobState.Failed:
                job.MarkFailed("boom");
                break;
        }

        Assert.Equal(expected, job.CanRestart);
    }
}
