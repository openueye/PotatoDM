using PotatoDM.AppCore.Job;
using PotatoDM.Core.Models;

namespace PotatoDM.App.Tests;

public class DownloadJobTests
{
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
