using Microsoft.Win32.SafeHandles;
using PotatoDM.Core.Exceptions;

namespace PotatoDM.Core.Storage;

/// <summary>
/// Reserves the target (or, when sparse, only its untouched holes) and writes
/// each segment directly at its absolute offset, so there is never a part-file
/// merge step.
/// </summary>
public sealed class RandomAccessOutputFile : IOutputFile
{
    private readonly string _workingPath;
    private SafeFileHandle? _handle;
    private bool _completed;

    private RandomAccessOutputFile(SafeFileHandle handle, string workingPath, long length, bool isSparse)
    {
        _handle = handle;
        _workingPath = workingPath;
        Length = length;
        IsSparse = isSparse;
    }

    public long Length { get; }

    /// <summary>True when the filesystem accepted the sparse flag; holes stay unallocated.</summary>
    public bool IsSparse { get; }

    public static RandomAccessOutputFile Create(string workingPath, long length, bool sparse = false)
    {
        try
        {
            EnsureParentDirectory(workingPath);
            var handle = File.OpenHandle(
                workingPath,
                FileMode.Create,
                FileAccess.Write,
                // Include FileShare.Delete so a pending delete can complete once the last handle closes on Windows.
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.Asynchronous,
                // Reserving the whole range would defeat the holes, so a sparse
                // file is extended with SetLength after the flag is applied.
                preallocationSize: sparse ? 0 : Math.Max(0, length));

            var isSparse = sparse && SparseFile.TryMarkSparse(handle);

            if (RandomAccess.GetLength(handle) != length)
            {
                RandomAccess.SetLength(handle, length);
            }

            return new RandomAccessOutputFile(handle, workingPath, length, isSparse);
        }
        catch (IOException ex)
        {
            throw new FileAccessException($"Could not create working file '{workingPath}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileAccessException($"Access denied creating working file '{workingPath}'.", ex);
        }
    }

    public static RandomAccessOutputFile OpenExisting(
        string workingPath,
        long expectedLength,
        bool sparse = false)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(
                workingPath,
                FileMode.Open,
                FileAccess.Write,
                // Include FileShare.Delete so a pending delete can complete once the last handle closes on Windows.
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.Asynchronous);

            var actualLength = RandomAccess.GetLength(handle);
            if (actualLength != expectedLength)
            {
                handle.Dispose();
                throw new StateException(
                    $"Working file '{workingPath}' is {actualLength} bytes but state expects " +
                    $"{expectedLength}. The file was modified externally; restart the job.");
            }

            // A job may have been started before sparse files were enabled; the
            // unwritten ranges still read as zeros after being turned into holes.
            var isSparse = sparse && SparseFile.TryMarkSparse(handle);

            return new RandomAccessOutputFile(handle, workingPath, expectedLength, isSparse);
        }
        catch (FileNotFoundException ex)
        {
            handle?.Dispose();
            throw new StateException($"Working file '{workingPath}' is missing; cannot resume.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            handle?.Dispose();
            throw new StateException($"Working file directory for '{workingPath}' is missing.", ex);
        }
        catch (IOException ex)
        {
            handle?.Dispose();
            throw new FileAccessException($"Could not open working file '{workingPath}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            handle?.Dispose();
            throw new FileAccessException($"Access denied opening working file '{workingPath}'.", ex);
        }
    }

    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        var handle = _handle ?? throw new ObjectDisposedException(nameof(RandomAccessOutputFile));
        return RandomAccess.WriteAsync(handle, buffer, offset, cancellationToken);
    }

    public void FlushToDisk()
    {
        var handle = _handle;

        if (handle is null)
        {
            return;
        }

        try
        {
            RandomAccess.FlushToDisk(handle);
        }
        catch (ObjectDisposedException)
        {
            // Completion or shutdown closed the handle concurrently.
        }
    }

    public async Task CompleteAsync(string targetPath, CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        // Make the body durable before the rename: a completed sidecar must
        // never describe a target file whose contents were still in the cache.
        FlushToDisk();
        DisposeHandle();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(_workingPath, targetPath, overwrite: false);
                _completed = true;
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(50 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FileAccessException(
                    $"Could not move '{_workingPath}' to '{targetPath}'.", ex);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeHandle();
        return ValueTask.CompletedTask;
    }

    private void DisposeHandle()
    {
        Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
