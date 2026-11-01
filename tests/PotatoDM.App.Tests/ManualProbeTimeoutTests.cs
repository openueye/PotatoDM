using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;

namespace PotatoDM.App.Tests;

/// <summary>
/// A manually added URL used to be probed with no deadline at all: a server that
/// accepted the connection and then never answered left the row at "探测中"
/// forever, with no cancel button and no error.
/// </summary>
public class ManualProbeTimeoutTests
{
    private static readonly Uri Url = new("https://example.test/file.bin");

    [Fact]
    public async Task Probe_that_never_returns_fails_the_job_instead_of_hanging()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine { OnProbe = HangAsync };
        var log = new RecordingLogSink();
        var manager = CreateManager(
            temp,
            engine,
            manualProbeTimeout: TimeSpan.FromMilliseconds(150),
            log: log);

        var started = DateTime.UtcNow;
        var job = await manager.AddAsync(Url, temp.FileFor("dl"), connections: 4)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var elapsed = DateTime.UtcNow - started;

        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            $"AddAsync took {elapsed.TotalSeconds:0.0}s, so the probe deadline did not fire.");
        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.Contains("超时", job.Error);
        Assert.Contains("0.2", job.Error);

        // "server never answered" must not read like "the user cancelled".
        Assert.DoesNotContain("已取消", job.Error);
        Assert.True(
            log.Has(LogLevel.Warning, "timed out"),
            "Nothing was logged for the probe timeout: " + log.Transcript);
    }

    [Fact]
    public async Task Cancelling_while_probing_still_reads_as_cancelled_not_timeout()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine { OnProbe = HangAsync };
        var manager = CreateManager(
            temp,
            engine,
            // Long enough that only an explicit cancel can end this test.
            manualProbeTimeout: TimeSpan.FromMinutes(5));

        using var cancellation = new CancellationTokenSource();
        var addTask = manager.AddAsync(
            Url,
            temp.FileFor("dl"),
            connections: 4,
            cancellationToken: cancellation.Token);

        await Task.Delay(100);
        cancellation.Cancel();

        var job = await addTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.Contains("已取消", job.Error);
        Assert.DoesNotContain("超时", job.Error);
    }

    [Fact]
    public async Task Browser_and_manual_probe_deadlines_are_independent()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine { OnProbe = HangAsync };

        // Browser deadline short, manual deadline long: the browser path must still
        // give up first. If the two shared one field, this would wait 5 minutes.
        var manager = CreateManager(
            temp,
            engine,
            browserProbeTimeout: TimeSpan.FromMilliseconds(150),
            manualProbeTimeout: TimeSpan.FromMinutes(5));

        var started = DateTime.UtcNow;
        var result = await manager
            .AddFromBrowserAsync(
                new BrowserDownloadContext
                {
                    Url = Url,
                    RequestId = "handoff-" + Guid.NewGuid().ToString("N"),
                })
            .WaitAsync(TimeSpan.FromSeconds(10));
        var elapsed = DateTime.UtcNow - started;

        Assert.False(result.Accepted);
        Assert.Equal("probe_timeout", result.Error);
        Assert.True(elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Manual_deadline_short_and_browser_deadline_long_also_works()
    {
        using var temp = new TempDirectory();
        var engine = new FakeDownloadEngine { OnProbe = HangAsync };

        // The mirror image: a manual add must give up on its own 150 ms deadline
        // even though the browser path is configured for five minutes.
        var manager = CreateManager(
            temp,
            engine,
            browserProbeTimeout: TimeSpan.FromMinutes(5),
            manualProbeTimeout: TimeSpan.FromMilliseconds(150));

        var started = DateTime.UtcNow;
        var job = await manager.AddAsync(Url, temp.FileFor("dl"), connections: 4)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(DownloadJobState.Failed, job.State);
        Assert.Contains("超时", job.Error);
        Assert.True(elapsed < TimeSpan.FromSeconds(5));
    }

    /// <summary>A probe that only ever ends when the token is cancelled.</summary>
    private static async Task<ProbeResult> HangAsync(Uri url, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }

    private static DownloadManager CreateManager(
        TempDirectory temp,
        FakeDownloadEngine engine,
        TimeSpan? browserProbeTimeout = null,
        TimeSpan? manualProbeTimeout = null,
        ILogSink? log = null)
    {
        return new DownloadManager(
            new InlineDispatcher(),
            new JobRegistryStore(temp.FileFor("jobs.json")),
            new FakeEngineFactory(engine),
            new AppSettings { Connections = 4, UseProxy = false },
            new FakeCookieProtector(),
            new JsonDownloadStateStore(),
            () => temp.FileFor("default"),
            browserProbeTimeout,
            manualProbeTimeout: manualProbeTimeout,
            log: log);
    }
}
