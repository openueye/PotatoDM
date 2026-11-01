namespace PotatoDM.Core.Storage;

/// <summary>Random-access destination file. Writes are addressed by absolute offset.</summary>
public interface IOutputFile : IAsyncDisposable
{
    long Length { get; }

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken);

    /// <summary>Flushes written bytes to stable storage; safe to call after completion.</summary>
    void FlushToDisk();

    /// <summary>Closes the handle and atomically promotes the working file to its final name.</summary>
    Task CompleteAsync(string targetPath, CancellationToken cancellationToken);
}
