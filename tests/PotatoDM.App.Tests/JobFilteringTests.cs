using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;

namespace PotatoDM.App.Tests;

public class JobFilteringTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData(JobFilter.All, true)]
    [InlineData(JobFilter.Downloading, true)]
    [InlineData(JobFilter.Completed, false)]
    [InlineData(JobFilter.Failed, false)]
    public void Downloading_filter_includes_probing_queued_and_downloading(
        JobFilter filter,
        bool expected)
    {
        var job = Job("movie.mkv", "https://example.test/movie.mkv");
        job.MarkDownloading();

        Assert.Equal(expected, JobFiltering.Matches(job, null, filter));
    }

    [Fact]
    public void Completed_and_failed_filters_match_exactly_that_state()
    {
        var completed = Job("done.mkv", "https://example.test/done.mkv");
        completed.MarkCompleted(100);

        var failed = Job("broken.mkv", "https://example.test/broken.mkv");
        failed.MarkFailed("connection reset");

        Assert.True(JobFiltering.Matches(completed, null, JobFilter.Completed));
        Assert.False(JobFiltering.Matches(completed, null, JobFilter.Failed));
        Assert.True(JobFiltering.Matches(failed, null, JobFilter.Failed));
        Assert.False(JobFiltering.Matches(failed, null, JobFilter.Completed));
    }

    [Fact]
    public void Search_matches_display_name_or_url_ignoring_case()
    {
        var job = Job("Movie.MKV", "https://example.test/folder/Movie.MKV");

        Assert.True(JobFiltering.Matches(job, "movie", JobFilter.All));
        Assert.True(JobFiltering.Matches(job, "EXAMPLE.TEST", JobFilter.All));
        Assert.False(JobFiltering.Matches(job, "archive", JobFilter.All));
    }

    private static DownloadJob Job(string fileName, string url)
    {
        var job = new DownloadJob(Hash, url, DateTimeOffset.UtcNow);
        job.BindTarget(
            Path.Combine(Path.GetTempPath(), fileName),
            Path.Combine(Path.GetTempPath(), fileName + ".download.json"));
        return job;
    }
}
