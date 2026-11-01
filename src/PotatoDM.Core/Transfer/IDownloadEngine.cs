using PotatoDM.Core.Models;

namespace PotatoDM.Core.Transfer;

public interface IDownloadEngine
{
    event EventHandler<DownloadProgress>? ProgressChanged;

    event EventHandler<JobStatus>? StateChanged;

    Task<ProbeResult> ProbeAsync(
        Uri url,
        RequestOptions options,
        CancellationToken cancellationToken = default);

    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default);

    Task<DownloadResult> ResumeAsync(
        string stateOrWorkingPath,
        ResumeOptions options,
        CancellationToken cancellationToken = default);
}
