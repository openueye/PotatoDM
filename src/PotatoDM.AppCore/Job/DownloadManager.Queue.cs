namespace PotatoDM.AppCore.Job;

public sealed partial class DownloadManager
{
    /// <summary>Cancels every active job and waits for the sidecars to be saved.</summary>
    public async Task PauseAllAsync(TimeSpan timeout)
    {
        List<Task> tasks;
        List<DownloadJob> queued;

        lock (_sync)
        {
            queued = _queue.Select(entry => entry.Job).ToList();
            _queue.Clear();

            foreach (var cancellation in _cancellations.Values)
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            tasks = _running.Values.ToList();
        }

        foreach (var job in queued)
        {
            _ui.Post(job.MarkPaused);
        }

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Pausing is the last step before most shutdowns, so make the registry
        // durable here instead of leaving it to the coalescing loop.
        await FlushRegistryAsync();
    }

    public Task ResumeAllAsync()
    {
        foreach (var job in _jobs.Where(job => job.CanResume).ToArray())
        {
            ScheduleResume(job);
        }

        return Task.CompletedTask;
    }

    /// <summary>Starts queued jobs after the concurrency limit was raised.</summary>
    public void RefreshQueue() => PumpQueue();

    /// <summary>Waits for running and queued transfers; useful for tests.</summary>
    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            Task[] tasks;

            lock (_sync)
            {
                if (_running.Count == 0 && _queue.Count == 0)
                {
                    return;
                }

                tasks = _running.Values.ToArray();
            }

            if (tasks.Length == 0)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return;
                }

                // Queued work with no worker can only mean a slot was freed
                // without a completion callback; pump once and re-check.
                PumpQueue();
                await Task.Yield();
                continue;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            try
            {
                await Task.WhenAll(tasks).WaitAsync(remaining);
            }
            catch (TimeoutException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void Enqueue(DownloadJob job, int connections, bool isResume, bool restart = false)
    {
        if (connections <= 0)
        {
            connections = Math.Max(1, _settings.Connections);
        }

        lock (_sync)
        {
            _queue.RemoveAll(entry => entry.Job.Id == job.Id);
            _queue.Add(new PendingStart(job, connections, isResume, restart));
            job.PendingFreshStart = !isResume;
            job.PendingConnections = connections;
            job.StartOverOnResume = false;
        }

        _ui.Post(job.MarkQueued);
        PumpQueue();
    }

    private void PumpQueue()
    {
        lock (_sync)
        {
            // A completed worker is removed by its continuation; pruning here
            // closes the gap so a freed slot is usable immediately.
            foreach (var completed in _running
                         .Where(pair => pair.Value.IsCompleted)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _running.Remove(completed);
            }

            var limit = Math.Max(1, _settings.MaxConcurrentJobs);

            while (_queue.Count > 0 && _running.Count < limit)
            {
                var next = _queue[0];
                _queue.RemoveAt(0);

                if (_removed.Contains(next.Job.Id))
                {
                    continue;
                }

                StartLocked(next);
            }
        }
    }

    private void StartLocked(PendingStart start)
    {
        var job = start.Job;

        if (_running.TryGetValue(job.Id, out var existing) && !existing.IsCompleted)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        job.Cancellation = cancellation;
        _cancellations[job.Id] = cancellation;

        var task = Task.Run(() =>
            RunAsync(job, start.Connections, start.IsResume, start.Restart, cancellation));
        _running[job.Id] = task;

        // Never run the continuation inline. OnTransferCompleted re-enters this
        // lock and pumps the queue again, so an inline continuation would run the
        // next job's start — and its completion — on the finishing thread's stack,
        // recursing once per queued job.
        //
        // That is latent rather than active, but only by scheduling luck. Inlining
        // needs the started task to be already complete here; measured on .NET 8
        // for ExecuteSynchronously, that lands 16.3% of registrations for a
        // Task.Run(Func<Task>) whose inner task completed synchronously, 6.6% for
        // Task.Run(Action), and 0% when the inner task faults or genuinely awaits.
        // Nothing guarantees RunAsync cannot finish synchronously, so the safety
        // of this call site must not rest on it.
        _ = task.ContinueWith(
            _ => OnTransferCompleted(job, task, cancellation),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void OnTransferCompleted(
        DownloadJob job,
        Task task,
        CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            if (_running.TryGetValue(job.Id, out var currentTask) &&
                ReferenceEquals(currentTask, task))
            {
                _running.Remove(job.Id);
            }

            if (_cancellations.TryGetValue(job.Id, out var current) &&
                ReferenceEquals(current, cancellation))
            {
                _cancellations.Remove(job.Id);
            }
        }

        if (ReferenceEquals(job.Cancellation, cancellation))
        {
            job.Cancellation = null;
        }

        cancellation.Dispose();
        PumpQueue();
    }

    private void ScheduleResume(DownloadJob job)
    {
        if (job.IsTorrent || job.IsMedia)
        {
            // These protocols resume from payload/cache files, without a sidecar.
            Enqueue(job, _settings.Connections, isResume: false);
        }
        else if (job.StartOverOnResume)
        {
            // The failed attempt's artifacts were deleted, so this is a new transfer.
            Enqueue(
                job,
                job.PendingConnections > 0 ? job.PendingConnections : _settings.Connections,
                isResume: false);
        }
        else if (File.Exists(job.StatePath))
        {
            Enqueue(job, _settings.Connections, isResume: true);
        }
        else if (job.PendingFreshStart)
        {
            Enqueue(job, job.PendingConnections, isResume: false);
        }
        else
        {
            job.MarkFailed("找不到状态文件，无法继续。");
        }
    }

    private async Task WaitForJobAsync(string id, TimeSpan timeout)
    {
        Task? task;

        lock (_sync)
        {
            task = _running.GetValueOrDefault(id);
        }

        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly record struct PendingStart(
        DownloadJob Job,
        int Connections,
        bool IsResume,
        // Carried here rather than on the job: Enqueue clears StartOverOnResume
        // on the way in, so a job field could not survive the trip.
        bool Restart = false);
}
