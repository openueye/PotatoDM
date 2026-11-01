using PotatoDM.AppCore.Models;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Models;
using PotatoDM.Torrent;

namespace PotatoDM.AppCore.Job;

public sealed partial class DownloadManager
{
    private const int TorrentChunkCount = 48;

    private async Task RunAsync(
        DownloadJob job,
        int connections,
        bool isResume,
        bool restart,
        CancellationTokenSource cancellation)
    {
        if (job.IsTorrent)
        {
            await RunTorrentAsync(job, cancellation);
            return;
        }

        var engine = _engines.Create();
        var requestOptions = CreateRequestOptions(job.RequestMetadata);
        job.PendingFreshStart = false;

        void OnProgress(object? sender, DownloadProgress progress) =>
            _ui.Post(() => job.ApplyProgress(progress));

        engine.ProgressChanged += OnProgress;

        try
        {
            _ui.Post(job.MarkDownloading);

            DownloadResult result;

            if (isResume)
            {
                var options = new ResumeOptions
                {
                    MaxConnectionsPerHost = _settings.MaxConnectionsPerHost,
                    MaxConnectionsPerJob = _settings.MaxConnectionsPerJob,
                    BandwidthLimiter = _bandwidthLimiter,
                    DynamicSegmentation = _settings.DynamicSegmentation,
                    SparseWorkingFile = _settings.SparseWorkingFile,
                    RequestOptions = requestOptions,
                };

                result = await engine.ResumeAsync(job.StatePath, options, cancellation.Token);
            }
            else
            {
                var request = new DownloadRequest
                {
                    Url = new Uri(job.Url),
                    TargetPath = job.TargetPath,
                    // Keep every sidecar/working file in the task's process folder.
                    StatePath = job.StatePath,
                    ProcessDirectory = NullIfEmpty(job.ProcessDirectory),
                    Connections = connections,
                    MaxConnectionsPerHost = _settings.MaxConnectionsPerHost,
                    MaxConnectionsPerJob = _settings.MaxConnectionsPerJob,
                    BandwidthLimiter = _bandwidthLimiter,
                    DynamicSegmentation = _settings.DynamicSegmentation,
                    SparseWorkingFile = _settings.SparseWorkingFile,
                    FfmpegPath = _settings.FfmpegPath,
                    RequestOptions = requestOptions,
                    // The engine deletes the old sidecar/working file inside its
                    // state lock; AppCore must not race it by deleting first.
                    Restart = restart,
                };

                result = await engine.DownloadAsync(request, cancellation.Token);
            }

            await CompleteJobAsync(job, result.TargetPath, result.TotalBytes);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _ui.Post(job.MarkPaused);
        }
        catch (Exception ex)
        {
            // The UI only shows ex.Message. Without this the stack trace of a real
            // transfer failure is lost the moment the message box appears.
            _log.Error($"Download job '{job.Id}' ({job.Url}) failed.", ex);
            CleanupAfterTransferFailure(job);
            PostFailure(job, ex.Message);
        }
        finally
        {
            engine.ProgressChanged -= OnProgress;
        }
    }

    private async Task RunTorrentAsync(DownloadJob job, CancellationTokenSource cancellation)
    {
        var downloadDirectory = job.TaskDirectory;
        if (string.IsNullOrWhiteSpace(downloadDirectory))
        {
            downloadDirectory = Path.GetDirectoryName(job.TargetPath) ?? _defaultFolder();
        }

        job.PendingFreshStart = false;

        try
        {
            _ui.Post(job.MarkDownloading);

            var result = await _torrentDownloader(
                new TorrentDownloadRequest
                {
                    Source = job.Url,
                    DownloadDirectory = downloadDirectory,
                    ProcessDirectory = NullIfEmpty(job.ProcessDirectory),
                    EnableDht = true,
                    EnablePeerExchange = true,
                },
                progress => _ui.Post(() =>
                {
                    job.SetDisplayName(progress.Name);
                    job.SetTorrentChecking(progress.IsChecking);
                    job.ApplyProgress(new DownloadProgress(
                        progress.BytesDownloaded,
                        progress.TotalBytes > 0 ? progress.TotalBytes : null,
                        progress.DownloadBytesPerSecond,
                        EstimateEta(
                            progress.BytesDownloaded,
                            progress.TotalBytes,
                            progress.DownloadBytesPerSecond),
                        progress.ConnectedPeers,
                        BuildTorrentChunks(progress.BytesDownloaded, progress.TotalBytes)));
                }),
                cancellation.Token);

            // Fill the chunk bar completely; some engines stop reporting right
            // before the last byte reaches the file.
            await _ui.InvokeAsync(() =>
            {
                job.ApplyProgress(new DownloadProgress(
                    result.TotalBytes,
                    result.TotalBytes,
                    0,
                    TimeSpan.Zero,
                    0,
                    BuildTorrentChunks(result.TotalBytes, result.TotalBytes)));
                return Task.CompletedTask;
            });

            await CompleteJobAsync(job, result.TargetPath, result.TotalBytes);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _ui.Post(job.MarkPaused);
        }
        catch (Exception ex)
        {
            // libtorrent re-hashes whatever is already on disk, so a failed torrent
            // is resumable too; keep its payload.
            _log.Error($"Torrent job '{job.Id}' ({job.Url}) failed.", ex);
            CleanupAfterTransferFailure(job);
            PostFailure(job, ex.Message);
        }
    }

    private RequestOptions CreateRequestOptions(JobRequestMetadata? metadata = null) => new()
    {
        UseProxy = _settings.UseProxy,
        Referer = metadata?.Referer,
        UserAgent = string.IsNullOrWhiteSpace(metadata?.UserAgent)
            ? "PotatoDM/1.0"
            : metadata.UserAgent,
        Cookies = metadata?.Cookies,
        ExtraHeaders = metadata?.ExtraHeaders,
    };

    /// <summary>
    /// libtorrent (via csdl) exposes only an overall fraction, not a piece
    /// map, so torrents are drawn as fixed chunks filled by byte progress.
    /// </summary>
    private static DownloadSegmentProgress[]? BuildTorrentChunks(
        long downloaded,
        long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return null;
        }

        var count = (int)Math.Max(1, Math.Min(TorrentChunkCount, totalBytes));
        var baseSize = totalBytes / count;
        var remainder = totalBytes % count;
        var chunks = new DownloadSegmentProgress[count];
        var offset = 0L;

        for (var index = 0; index < count; index++)
        {
            var length = baseSize + (index < remainder ? 1 : 0);
            chunks[index] = new DownloadSegmentProgress(
                index,
                Math.Clamp(downloaded - offset, 0, length),
                length);
            offset += length;
        }

        return chunks;
    }

    private static TimeSpan? EstimateEta(long downloaded, long totalBytes, long bytesPerSecond)
    {
        if (totalBytes <= 0 || bytesPerSecond <= 0)
        {
            return null;
        }

        var remaining = totalBytes - downloaded;
        return remaining <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(remaining / (double)bytesPerSecond);
    }

    private Task CompleteJobAsync(DownloadJob job, string? targetPath, long totalBytes)
    {
        PostCompletion(job, targetPath, totalBytes);

        // Queue persistence behind the dispatcher's completion update and wait
        // until the save request is visible before the worker finishes.
        return _ui.InvokeAsync(() =>
        {
            RequestRegistrySave();
            return Task.CompletedTask;
        });
    }

    private void PostCompletion(DownloadJob job, string? targetPath, long totalBytes)
    {
        _ui.Post(() =>
        {
            if (!string.IsNullOrWhiteSpace(targetPath) &&
                !string.Equals(job.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                job.BindTarget(targetPath, job.StatePath);
                UpdateRecordTarget(job.Id, targetPath);
            }

            MarkRecordCompleted(job.Id);
            job.MarkCompleted(totalBytes);
        });
    }

    private void PostFailure(DownloadJob job, string message)
    {
        // A failed transfer keeps its sidecar, so report what is actually on disk.
        // Hardcoding true would claim a sidecar for torrent/media jobs, which never
        // write one; hardcoding false would silently downgrade "resume" to "start
        // over" while still appearing to work.
        var hasStateFile = !string.IsNullOrEmpty(job.StatePath) && File.Exists(job.StatePath);

        _ui.Post(() =>
        {
            job.SetHasStateFile(hasStateFile);
            job.StartOverOnResume = false;
            job.MarkFailed(message);
        });
    }
}
