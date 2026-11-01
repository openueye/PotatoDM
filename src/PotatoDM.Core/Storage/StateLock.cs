using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Exceptions;

namespace PotatoDM.Core.Storage;

/// <summary>
/// An exclusive lock beside the state file, so two processes can never drive
/// the same job at once.
/// </summary>
public sealed class StateLock : IDisposable
{
    private FileStream? _stream;

    private StateLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <param name="statePath">Path of the sidecar whose job this lock protects.</param>
    /// <param name="log">
    /// Optional sink. Contention is logged at Debug: a second process driving the
    /// same job is a real event worth seeing, but it is the expected outcome of
    /// double-clicking a sidecar, not a fault.
    /// </param>
    public static StateLock Acquire(string statePath, ILogSink? log = null)
    {
        var lockPath = statePath + ".lock";
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                // Deliberately FileShare.None, unlike the working-file handles: this
                // handle IS the mutex. Allowing FileShare.Delete would let another
                // process delete the lock file out from under us and create a fresh
                // one, so two processes could drive the same job.
                FileShare.None);
            return new StateLock(stream);
        }
        catch (IOException ex)
        {
            log.Debug($"State lock '{lockPath}' is held by another process.", ex);

            throw new JobAlreadyRunningException(
                $"Another process is already working on '{statePath}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Error($"Access denied creating the state lock for '{statePath}'.", ex);

            throw new FileAccessException($"Access denied creating lock for '{statePath}'.", ex);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
