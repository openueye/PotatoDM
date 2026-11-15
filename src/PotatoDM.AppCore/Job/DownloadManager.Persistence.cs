using System.Text.Json;
using PotatoDM.AppCore.Models;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Media;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;
using PotatoDM.Torrent;

namespace PotatoDM.AppCore.Job;

public sealed partial class DownloadManager
{
    private static readonly JsonSerializerOptions CookieJsonOptions = new(JsonSerializerDefaults.Web);

    // Registry writes go through one coalescing loop. Rewriting the whole list per
    // add and per completion was O(n²) in a burst: 800 instant jobs spent ~40 s in
    // JSON serialization alone. Callers request a save and never block on disk I/O;
    // FlushRegistryAsync is the explicit durability point.
    private static readonly TimeSpan RegistrySaveCoalesceDelay = TimeSpan.FromMilliseconds(100);

    private readonly object _flushLock = new();

    private Task _flushLoop = Task.CompletedTask;

    private long _registryVersion;

    private long _persistedVersion;

    /// <summary>Restores the job list from the registry and each engine sidecar.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var records = await _registry.LoadAsync(cancellationToken);
        var keptRecords = new List<JobRecord>();
        var restoredJobs = new List<DownloadJob>();

        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Id) ||
                string.IsNullOrWhiteSpace(record.Url) ||
                string.IsNullOrWhiteSpace(record.TargetPath) ||
                string.IsNullOrWhiteSpace(record.StatePath))
            {
                continue;
            }

            var job = await RestoreAsync(record, cancellationToken);
            if (job is null)
            {
                continue;
            }

            keptRecords.Add(record);
            restoredJobs.Add(job);
        }

        lock (_sync)
        {
            _records.Clear();
            _records.AddRange(keptRecords);
        }

        _jobs.Clear();

        foreach (var job in restoredJobs)
        {
            _jobs.Add(job);
        }

        lock (_sync)
        {
            _reservedTargets.Clear();
            foreach (var job in restoredJobs)
            {
                _reservedTargets.Add(job.TargetPath);
            }

            _browserResults.Clear();
        }

        if (keptRecords.Count != records.Count)
        {
            RequestRegistrySave();
        }
    }

    private async Task<DownloadJob?> RestoreAsync(
        JobRecord record,
        CancellationToken cancellationToken)
    {
        JobState? state;

        try
        {
            state = await _stateStore.LoadAsync(record.StatePath, cancellationToken);
        }
        catch (StateException)
        {
            state = null;
        }

        var taskDirectory = !string.IsNullOrWhiteSpace(record.TaskDirectory)
            ? record.TaskDirectory
            : StatePaths.TaskDirectoryFor(record.TargetPath, record.StatePath);

        var job = new DownloadJob(record.Id, record.Url, record.CreatedUtc)
        {
            IsMedia = record.IsMedia,
            IsTorrent = record.IsTorrent,
        };
        if (record.IsTorrent && TorrentSource.IsMagnet(record.Url))
        {
            job.SetDisplayName(TorrentSource.SuggestedName(record.Url));
        }
        job.BindTarget(record.TargetPath, record.StatePath, taskDirectory);
        job.RequestMetadata = new JobRequestMetadata
        {
            Referer = record.Referer,
            UserAgent = record.UserAgent,
            Cookies = UnprotectCookies(record.ProtectedCookies),
            ExtraHeaders = record.ExtraHeaders,
        };

        if (record.IsTorrent)
        {
            job.SetHasStateFile(false);

            if (record.IsCompleted)
            {
                var payloadBytes = PayloadSize(record.TargetPath);

                if (payloadBytes > 0)
                {
                    job.MarkCompleted(payloadBytes);
                }
                else
                {
                    job.StartOverOnResume = true;
                    job.MarkFailed("下载文件已被移动或删除。");
                }

                return job;
            }

            // libtorrent preallocates the payload, so the file/directory can
            // exist while the transfer is far from done. Only the persisted
            // completion flag proves the job finished.
            job.MarkPaused();
            return job;
        }

        if (record.IsMedia)
        {
            if (File.Exists(record.TargetPath))
            {
                job.MarkCompleted(new FileInfo(record.TargetPath).Length);
                return job;
            }

            // Partially downloaded segments survive here, so the job can resume.
            if (Directory.Exists(MediaCacheDirectory(job)) ||
                Directory.Exists(record.TargetPath + MediaDownloader.CacheSuffix))
            {
                job.SetHasStateFile(false);
                job.MarkPaused();
                return job;
            }

            CleanupAfterFailure(job);
            return null;
        }

        if (state is null)
        {
            if (!File.Exists(record.TargetPath))
            {
                CleanupAfterFailure(job);
                return null;
            }

            job.MarkCompleted(new FileInfo(record.TargetPath).Length);
            return job;
        }

        if (!string.IsNullOrWhiteSpace(state.TargetFile))
        {
            job.BindTarget(state.TargetFile, record.StatePath);
        }

        job.SetHasStateFile(true);
        job.ApplyProgress(new DownloadProgress(
            state.BytesDownloaded,
            state.TotalLength,
            0,
            null,
            0,
            state.Segments
                .Select(segment => new DownloadSegmentProgress(
                    segment.Index,
                    segment.BytesDone,
                    segment.Length))
                .ToArray()));

        switch (state.Status)
        {
            case JobStatus.Completed:
                if (File.Exists(job.TargetPath))
                {
                    job.MarkCompleted(state.TotalLength);
                }
                else
                {
                    job.MarkFailed("目标文件已被移动或删除。");
                }

                break;

            case JobStatus.Failed:
                // The sidecar and partial payload outlived the failure, so the row
                // comes back resumable instead of losing its progress on restart.
                job.SetHasStateFile(true);
                job.StartOverOnResume = false;
                job.MarkFailed("上次下载失败，可以继续或重新开始。");
                break;

            default:
                // Paused, or Downloading from a session that ended without a clean save.
                job.MarkPaused();
                break;
        }

        return job;
    }

    private void UpdateRecordTarget(string jobId, string targetPath)
    {
        lock (_sync)
        {
            var record = _records.FirstOrDefault(item => item.Id == jobId);
            if (record is null)
            {
                return;
            }

            _reservedTargets.Remove(record.TargetPath);
            _reservedTargets.Add(targetPath);
            record.TargetPath = targetPath;
        }
    }

    private string? ProtectCookies(IReadOnlyList<RequestCookie>? cookies)
    {
        if (cookies is null || cookies.Count == 0)
        {
            return null;
        }

        try
        {
            var json = JsonSerializer.Serialize(cookies, CookieJsonOptions);
            return _cookieProtector.Protect(json);
        }
        catch (Exception ex)
        {
            // Losing persisted cookies is better than failing the download: the
            // transfer itself already has them in memory. Degradation, not a fault.
            _log.Debug("Cookies could not be protected; they will not survive a restart.", ex);
            return null;
        }
    }

    private List<RequestCookie>? UnprotectCookies(string? protectedCookies)
    {
        if (string.IsNullOrWhiteSpace(protectedCookies))
        {
            return null;
        }

        try
        {
            var json = _cookieProtector.Unprotect(protectedCookies);
            if (string.IsNullOrWhiteSpace(json))
            {
                // The production protector reports failure by returning null rather
                // than throwing, so this branch — not the catch below — is where a
                // real DPAPI failure lands. Without it an undecryptable cookie blob
                // is indistinguishable from "this job never had cookies", and the
                // user just sees a 401/403 after a restart.
                _log.Warning(
                    "Stored cookies could not be decrypted for a restored job; " +
                    "the download will run without them.");

                return null;
            }

            return JsonSerializer.Deserialize<List<RequestCookie>>(json, CookieJsonOptions);
        }
        catch (Exception ex)
        {
            _log.Warning(
                "Stored cookies could not be decrypted for a restored job; " +
                "the download will run without them.",
                ex);

            return null;
        }
    }

    /// <summary>
    /// Marks the registry dirty. The write itself happens on a background loop that
    /// coalesces a burst into one pass, so callers are never blocked on disk I/O.
    /// </summary>
    private void RequestRegistrySave()
    {
        lock (_flushLock)
        {
            // Read by the loop while it holds only _sync, so keep the bump atomic
            // rather than relying on the two locks lining up.
            Interlocked.Increment(ref _registryVersion);
            EnsureFlushLoopLocked();
        }
    }

    /// <summary>
    /// Waits for the coalescing loop to catch up with every change requested so far.
    /// Write failures are logged and not retried, as before, so this is a durability
    /// point rather than a guarantee that the file was replaced. Used before
    /// shutdown, and by tests that read <c>jobs.json</c> directly.
    /// </summary>
    public async Task FlushRegistryAsync(CancellationToken cancellationToken = default)
    {
        Task loop;
        long target;

        lock (_flushLock)
        {
            target = _registryVersion;
            loop = _persistedVersion >= target ? Task.CompletedTask : EnsureFlushLoopLocked();
        }

        while (true)
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_flushLock)
            {
                if (_persistedVersion >= target)
                {
                    return;
                }

                // The loop we awaited had already finished its last pass when the
                // request arrived; start one that sees it.
                loop = EnsureFlushLoopLocked();
            }
        }
    }

    private Task EnsureFlushLoopLocked()
    {
        if (_flushLoop.IsCompleted)
        {
            _flushLoop = FlushRegistryLoopAsync();
        }

        return _flushLoop;
    }

    private async Task FlushRegistryLoopAsync()
    {
        // Let a burst of adds finish first, so N requests become one write instead
        // of N full-list rewrites.
        await Task.Delay(RegistrySaveCoalesceDelay).ConfigureAwait(false);

        while (true)
        {
            long version;
            List<JobRecord> snapshot;

            lock (_sync)
            {
                version = Volatile.Read(ref _registryVersion);
                snapshot = _records.ToList();
            }

            try
            {
                await _registry.SaveAsync(snapshot).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _log.Warning("The job list could not be saved; it will be incomplete after a restart.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warning("The job list could not be saved; it will be incomplete after a restart.", ex);
            }

            lock (_flushLock)
            {
                if (version > _persistedVersion)
                {
                    _persistedVersion = version;
                }

                if (version == Volatile.Read(ref _registryVersion))
                {
                    return;
                }
            }
        }
    }

    private void RegisterJob(
        DownloadJob job,
        JobPaths layout,
        long totalBytes,
        bool isMedia,
        bool isTorrent,
        JobRequestMetadata? metadata)
    {
        job.IsMedia = isMedia;
        job.IsTorrent = isTorrent;
        job.ResolveTarget(layout.TargetPath, layout.StatePath, isMedia ? 0 : totalBytes, layout.TaskDirectory);

        lock (_sync)
        {
            _records.Add(new JobRecord
            {
                Id = job.Id,
                Url = job.Url,
                TargetPath = layout.TargetPath,
                StatePath = layout.StatePath,
                TaskDirectory = layout.TaskDirectory,
                IsMedia = isMedia,
                IsTorrent = isTorrent,
                CreatedUtc = job.CreatedUtc,
                Referer = metadata?.Referer,
                UserAgent = metadata?.UserAgent,
                ProtectedCookies = ProtectCookies(metadata?.Cookies),
                ExtraHeaders = metadata?.ExtraHeaders is null
                    ? null
                    : new Dictionary<string, string>(metadata.ExtraHeaders),
            });
        }

        RequestRegistrySave();
    }

    private void MarkRecordCompleted(string jobId)
    {
        lock (_sync)
        {
            var record = _records.FirstOrDefault(item => item.Id == jobId);

            if (record is not null)
            {
                record.IsCompleted = true;
            }
        }
    }
}
