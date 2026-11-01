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

    public static StateLock Acquire(string statePath)
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
                FileShare.None);
            return new StateLock(stream);
        }
        catch (IOException ex)
        {
            throw new JobAlreadyRunningException(
                $"Another process is already working on '{statePath}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileAccessException($"Access denied creating lock for '{statePath}'.", ex);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
