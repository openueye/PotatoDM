using System.Security;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Media;
using PotatoDM.Core.Storage;

namespace PotatoDM.AppCore.Job;

public sealed partial class DownloadManager
{
    private JobPaths ReserveLayout(string folder, string? suggestedFileName)
    {
        var layout = JobLayout.Create(folder, suggestedFileName, IsTargetReserved);

        lock (_sync)
        {
            _reservedTargets.Add(layout.TaskDirectory);
            _reservedTargets.Add(layout.TargetPath);
        }

        return layout;
    }

    private void ReleaseReservations(DownloadJob job)
    {
        lock (_sync)
        {
            _reservedTargets.Remove(job.TargetPath);

            if (!string.IsNullOrEmpty(job.TaskDirectory))
            {
                _reservedTargets.Remove(job.TaskDirectory);
            }
        }
    }

    private bool IsTargetReserved(string path)
    {
        lock (_sync)
        {
            return _reservedTargets.Contains(path);
        }
    }

    private static string MediaCacheDirectory(DownloadJob job) =>
        string.IsNullOrEmpty(job.TaskDirectory)
            ? job.TargetPath + MediaDownloader.CacheSuffix
            : Path.Combine(
                job.TaskDirectory,
                StatePaths.ProcessDirectoryName,
                StatePaths.MediaCacheDirectoryName);

    /// <summary>
    /// Removes the process folder but keeps the finished file (if any). Used
    /// when a completed task is removed from the list.
    /// </summary>
    private void DeleteProcessArtifacts(DownloadJob job)
    {
        if (!string.IsNullOrEmpty(job.TaskDirectory))
        {
            DeleteDirectory(Path.Combine(job.TaskDirectory, StatePaths.ProcessDirectoryName));
            DeleteDirectoryIfEmpty(job.TaskDirectory);
            return;
        }

        DeleteLegacyArtifacts(job);
    }

    /// <summary>Removes the whole task folder, including any partial payload.</summary>
    private void DeleteTaskDirectory(DownloadJob job)
    {
        if (!string.IsNullOrEmpty(job.TaskDirectory))
        {
            DeleteDirectory(job.TaskDirectory);
            return;
        }

        DeleteLegacyArtifacts(job);
    }

    /// <summary>
    /// Removes every artifact for a job that never produced resumable state —
    /// probe and registration failures, and records whose files are already gone.
    /// Transfer failures use <see cref="CleanupAfterTransferFailure"/> instead.
    /// </summary>
    private void CleanupAfterFailure(DownloadJob job)
    {
        DeleteTaskDirectory(job);
        job.StartOverOnResume = true;
    }

    /// <summary>
    /// A failed transfer keeps everything it can be resumed from: the sidecar, its
    /// backup and the partial payload. Throwing away a multi-gigabyte transfer that
    /// died at 95% is the most expensive thing this app can do to a user, and the
    /// engine already knows how to continue from a failed job. Only per-attempt
    /// scratch files go.
    /// </summary>
    private void CleanupAfterTransferFailure(DownloadJob job)
    {
        if (!string.IsNullOrEmpty(job.StatePath))
        {
            // Torn write from a crash mid-save; the real sidecar and its .bak are
            // authoritative and stay.
            FileSystemHelpers.TryDeleteFile(job.StatePath + ".tmp", _log);
        }

        job.StartOverOnResume = false;
    }

    /// <summary>Cleanup for records written before the per-task folder layout.</summary>
    private void DeleteLegacyArtifacts(DownloadJob job)
    {
        if (!string.IsNullOrEmpty(job.StatePath))
        {
            FileSystemHelpers.TryDeleteFile(job.StatePath, _log);
            FileSystemHelpers.TryDeleteFile(job.StatePath + ".bak", _log);
            FileSystemHelpers.TryDeleteFile(job.StatePath + ".tmp", _log);
            FileSystemHelpers.TryDeleteFile(job.StatePath + ".lock", _log);
        }

        if (string.IsNullOrEmpty(job.TargetPath))
        {
            return;
        }

        FileSystemHelpers.TryDeleteFile(StatePaths.WorkingFor(job.TargetPath), _log);

        if (job.IsMedia)
        {
            DeleteDirectory(job.TargetPath + MediaDownloader.CacheSuffix);
        }
        else if (job.IsTorrent)
        {
            // Torrent payload files are written directly; the metadata cache
            // was shared by every torrent in the folder in the old layout.
            if (Directory.Exists(job.TargetPath))
            {
                DeleteDirectory(job.TargetPath);
            }
            else
            {
                FileSystemHelpers.TryDeleteFile(job.TargetPath, _log);
            }
        }
    }

    private void DeleteDirectoryIfEmpty(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _log.Debug($"Empty directory '{path}' could not be removed.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Debug($"Empty directory '{path}' could not be removed.", ex);
        }
    }

    /// <summary>
    /// Deletes a directory tree, retrying while a freshly closed handle still holds
    /// the last file. The final attempt's failure is logged rather than dropped:
    /// this is the difference between "the task folder is gone" and "gigabytes of
    /// orphaned scratch data nobody will ever find".
    /// </summary>
    private void DeleteDirectory(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (
                attempt < 4 && ex is IOException or UnauthorizedAccessException)
            {
                // A handle may still be closing right after cancellation.
                Thread.Sleep(100 * (attempt + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning(
                    $"Directory '{path}' could not be deleted after 5 attempts; " +
                    "it is left on disk.",
                    ex);

                return;
            }
        }
    }

    private long PayloadSize(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (Directory.Exists(path))
            {
                return Directory
                    .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Reporting 0 bytes makes a restored job look empty; the row is still
            // resumable, so this is cosmetic degradation, not a failure.
            _log.Debug($"Could not measure the payload at '{path}'; reporting 0 bytes.", ex);
        }

        return 0;
    }
}
