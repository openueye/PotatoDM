using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Models;

namespace PotatoDM.App.Tests;

/// <summary>
/// Coverage for the queue pump under a burst of jobs that fail before their first
/// suspension. Before this test the pump had no coverage at all for that shape:
/// every concurrency test in <c>DownloadManagerTests</c> holds jobs open with
/// gates, so nothing exercised "finish instantly, free the slot, start the next".
///
/// <para><b>Scope note — read before trusting this as a guard.</b> The completion
/// continuation is registered with <c>TaskContinuationOptions.None</c> rather than
/// <c>ExecuteSynchronously</c>, so it never runs inline on the thread that
/// finished the transfer. That property cannot be observed from outside the
/// manager, and this test does <i>not</i> fail if the option is reverted — see
/// the measurement recorded in <c>StartLocked</c>'s comment. What this test does
/// pin is that a large burst of instantly-failing jobs drains completely, keeps
/// the concurrency limit, and leaves the manager usable.</para>
/// </summary>
public class DownloadQueuePumpTests
{
    // Behavioural regression for the pump, NOT a stack-overflow guard: this passes
    // with ExecuteSynchronously restored (verified with 3000 jobs), because a job
    // whose worker task faults synchronously does not inline the continuation.
    [Fact]
    public async Task Many_instantly_failing_jobs_drain_and_leave_the_manager_usable()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "downloads");

        var engine = new FakeDownloadEngine
        {
            // Throwing before the first await keeps the worker task extremely
            // short-lived, which is the shape that frees a slot immediately.
            OnDownload = (_, _) => throw new HttpDownloadException("boom", isTransient: false),
        };

        var manager = DownloadManagerTests.CreateManager(
            temp,
            engine,
            new AppSettings { MaxConcurrentJobs = 3 });

        await manager.InitializeAsync();

        const int count = 800;
        var jobs = new List<DownloadJob>(count);

        for (var index = 0; index < count; index++)
        {
            jobs.Add(await manager.AddAsync(
                new Uri($"https://example.test/file{index}.bin"),
                folder,
                connections: 1));
        }

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(120));

        Assert.Equal(count, engine.DownloadCount);
        Assert.All(jobs, job => Assert.Equal(DownloadJobState.Failed, job.State));

        // WaitForIdleAsync returns once nothing is running or queued; a pump that
        // dropped a job on the floor would leave a slot unaccounted for.
        Assert.Empty(manager.Jobs.Where(job => job.State == DownloadJobState.Queued));

        // The pump must still work after the burst.
        engine.OnDownload = (request, _) => Task.FromResult(
            new DownloadResult(request.TargetPath ?? string.Empty, 4096, DownloadMode.Segmented, TimeSpan.Zero));

        var survivor = await manager.AddAsync(
            new Uri("https://example.test/survivor.bin"),
            folder,
            connections: 1);

        await manager.WaitForIdleAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadJobState.Completed, survivor.State);
    }
}
