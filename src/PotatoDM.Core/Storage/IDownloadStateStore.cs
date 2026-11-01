using PotatoDM.Core.Models;

namespace PotatoDM.Core.Storage;

public interface IDownloadStateStore
{
    /// <summary>Returns null when neither the state file nor its backup exists.</summary>
    Task<JobState?> LoadAsync(string statePath, CancellationToken cancellationToken);

    /// <summary>Persists a full snapshot atomically (temp file + replace + backup).</summary>
    Task SaveAsync(string statePath, JobState state, CancellationToken cancellationToken);
}
