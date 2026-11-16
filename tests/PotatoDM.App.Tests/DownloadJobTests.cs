using PotatoDM.AppCore.Job;
using PotatoDM.Core.Models;
using PotatoDM.Core.Exceptions;

namespace PotatoDM.App.Tests;

public class DownloadJobTests
{
    [Fact]
    public void Failure_categories_give_specific_recovery_and_block_unsupported_retry()
    {
        Assert.Equal(JobFailureKind.Authentication, JobFailure.Classify(new HttpDownloadException("HTTP 403 (Forbidden)", false)));
        Assert.Equal(JobFailureKind.Storage, JobFailure.Classify(new DiskSpaceException("full")));
        Assert.Equal(JobFailureKind.MediaTool, JobFailure.Classify(new MediaMergeException("ffmpeg")));
        Assert.Equal(JobFailureKind.UnsupportedMedia, JobFailure.Classify(new DrmProtectedException("test")));
        var job = new DownloadJob("job", "https://example.test/file", DateTimeOffset.UtcNow);
        job.MarkFailed("unsupported", JobFailureKind.UnsupportedMedia);
        Assert.False(job.CanRestart);
        Assert.Contains("不受支持", job.FailureSummary);
    }

    [Fact]
    public void Finalizing_progress_is_not_presented_as_completed()
    {
        var job = new DownloadJob("job", "https://example.test/file", DateTimeOffset.UtcNow);
        job.MarkDownloading();
        job.ApplyProgress(new DownloadProgress(100, 100, 0, null, 0, IsFinalizing: true));
        Assert.True(job.IsFinalizing);
        Assert.Equal("合并中", job.StateText);
        Assert.Equal("合并中", job.ProgressText);
        job.MarkCompleted(100);
        Assert.False(job.IsFinalizing);
    }
    [Fact]
    public void ApplyProgress_exposes_each_real_download_segment_for_the_task_row()
    {
        var job = new DownloadJob("job-1", "https://example.test/archive.zip", DateTimeOffset.UtcNow);

        job.ApplyProgress(new DownloadProgress(
            BytesDownloaded: 1_500,
            TotalBytes: 4_000,
            BytesPerSecond: 2_048,
            Eta: TimeSpan.FromSeconds(2),
            ActiveConnections: 2,
            Segments:
            [
                new DownloadSegmentProgress(Index: 0, BytesDownloaded: 1_000, TotalBytes: 2_000),
                new DownloadSegmentProgress(Index: 1, BytesDownloaded: 500, TotalBytes: 2_000),
            ]));

        Assert.True(job.HasSegmentProgress);
        Assert.Equal(2, job.SegmentProgresses.Count);
        Assert.Equal(50, job.SegmentProgresses[0].Percent);
        Assert.Equal(25, job.SegmentProgresses[1].Percent);
    }

    [Fact]
    public void Reapplying_an_identical_progress_snapshot_raises_nothing()
    {
        var job = new DownloadJob("job-3", "https://example.test/archive.zip", DateTimeOffset.UtcNow);
        var progress = new DownloadProgress(
            BytesDownloaded: 1_500,
            TotalBytes: 4_000,
            BytesPerSecond: 2_048,
            Eta: TimeSpan.FromSeconds(2),
            ActiveConnections: 2,
            Segments:
            [
                new DownloadSegmentProgress(Index: 0, BytesDownloaded: 1_000, TotalBytes: 2_000),
                new DownloadSegmentProgress(Index: 1, BytesDownloaded: 500, TotalBytes: 2_000),
            ]);

        job.ApplyProgress(progress);

        var raised = new List<string?>();
        job.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        job.ApplyProgress(progress);

        Assert.Empty(raised);
    }

    [Fact]
    public void Long_eta_is_shown_in_days()
    {
        var job = new DownloadJob("job-2", "https://example.test/archive.zip", DateTimeOffset.UtcNow);
        job.MarkDownloading();
        job.ApplyProgress(new DownloadProgress(
            BytesDownloaded: 1_000,
            TotalBytes: 10_000,
            BytesPerSecond: 1,
            Eta: TimeSpan.FromDays(76) + TimeSpan.FromMinutes(5),
            ActiveConnections: 3));

        Assert.Equal("76天 00:05", job.EtaText);
    }
}
