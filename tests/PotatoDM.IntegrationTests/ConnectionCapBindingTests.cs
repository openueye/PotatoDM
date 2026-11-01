using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Storage;
using PotatoDM.Tests;

namespace PotatoDM.IntegrationTests;

/// <summary>
/// Proves that <c>AppSettings.MaxConnectionsPerJob</c> is not decorative: the value
/// a user types reaches the engine's connection limiter and changes how many
/// sockets the server actually sees at once.
/// </summary>
/// <remarks>
/// The measurement is taken on the server side (<c>/admin/stats</c> reports the peak
/// number of overlapping <c>/dl</c> requests), not from a counter inside the app, so
/// a limiter that is created and never consulted cannot pass.
/// </remarks>
[Collection(TestServerCollection.Name)]
public class ConnectionCapBindingTests
{
    private const long FileSize = 4L * 1024 * 1024;
    private const long ThrottleBytesPerSecond = 1024L * 1024;

    private readonly TestServerFixture _server;

    public ConnectionCapBindingTests(TestServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public async Task Setting_the_per_job_cap_to_two_holds_the_server_to_two_connections()
    {
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url(
            $"/dl?token={token}&size={FileSize}&throttle={ThrottleBytesPerSecond}"));

        using var temp = new TempDirectory();
        var settings = new AppSettings
        {
            Connections = 8,
            AutomaticConnections = false,
            MaxConnectionsPerHost = 8,
            MaxConnectionsPerJob = 2,
            UseProxy = false,
        };

        var peak = await DownloadAndMeasurePeakAsync(temp, settings, token, url);

        Assert.True(
            peak <= settings.MaxConnectionsPerJob,
            $"The server saw {peak} overlapping requests with the per-job cap set to " +
            $"{settings.MaxConnectionsPerJob}.");
        Assert.True(peak >= 2, $"Peak was {peak}; the job never even used two connections.");
    }

    [Fact]
    public async Task Raising_the_cap_lets_more_connections_run()
    {
        var token = IntegrationHelpers.NewToken();
        var url = new Uri(_server.Url(
            $"/dl?token={token}&size={FileSize}&throttle={ThrottleBytesPerSecond}"));

        using var temp = new TempDirectory();
        var settings = new AppSettings
        {
            Connections = 8,
            AutomaticConnections = false,
            MaxConnectionsPerHost = 8,
            MaxConnectionsPerJob = AppSettings.MaxConnectionsPerJobCeiling,
            UseProxy = false,
        };

        var peak = await DownloadAndMeasurePeakAsync(temp, settings, token, url);

        // Control group. Without it, "peak <= 2" would also pass on a build that
        // simply serialised every request.
        Assert.True(
            peak >= 3,
            $"Peak was {peak} with the cap raised to {settings.MaxConnectionsPerJob}: " +
            "the cap is not what limits the first test.");
    }

    private async Task<int> DownloadAndMeasurePeakAsync(
        TempDirectory temp,
        AppSettings settings,
        string token,
        Uri url)
    {
        var manager = new DownloadManager(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json")),
            new DownloadEngineFactory(),
            settings,
            new PassthroughCookieProtector(),
            new JsonDownloadStateStore(),
            () => temp.Path);

        var job = await manager.AddAsync(url, temp.FileFor("dl"), connections: 8);
        Assert.True(
            job.State != DownloadJobState.Failed,
            $"The job failed before it started: {job.State} {job.Error}");

        var completed = await IntegrationHelpers.WaitUntilAsync(
            () => Task.FromResult(job.State == DownloadJobState.Completed),
            TimeSpan.FromSeconds(90));

        Assert.True(
            completed,
            $"The job never completed: state {job.State}, error '{job.Error}'.");

        return (await _server.GetStatsAsync(token)).MaxConcurrent;
    }
}
