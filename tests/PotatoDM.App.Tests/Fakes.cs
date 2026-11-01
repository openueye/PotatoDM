using PotatoDM.AppCore.Services;
using PotatoDM.Core.Models;
using PotatoDM.Core.Transfer;

namespace PotatoDM.App.Tests;

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();

    public Task InvokeAsync(Func<Task> action) => action();

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
}

internal sealed class FakeCookieProtector : ICookieProtector
{
    public string? Protect(string plaintext) => "protected:" + plaintext;

    public string? Unprotect(string? protectedText) =>
        protectedText is not null && protectedText.StartsWith("protected:", StringComparison.Ordinal)
            ? protectedText["protected:".Length..]
            : null;
}

internal sealed class FakeEngineFactory : IDownloadEngineFactory
{
    private readonly FakeDownloadEngine _engine;

    public FakeEngineFactory(FakeDownloadEngine engine)
    {
        _engine = engine;
    }

    public IDownloadEngine Create() => _engine;
}

internal sealed class FakeDownloadEngine : IDownloadEngine
{
    private int _probeCount;
    private int _downloadCount;
    private int _resumeCount;

    public Func<Uri, CancellationToken, Task<ProbeResult>>? OnProbe { get; set; }

    public Func<DownloadRequest, CancellationToken, Task<DownloadResult>>? OnDownload { get; set; }

    public Func<string, ResumeOptions, CancellationToken, Task<DownloadResult>>? OnResume { get; set; }

    public int ProbeCount => Volatile.Read(ref _probeCount);

    public int DownloadCount => Volatile.Read(ref _downloadCount);

    public int ResumeCount => Volatile.Read(ref _resumeCount);

    public RequestOptions? LastProbeOptions { get; private set; }

    public DownloadRequest? LastDownloadRequest { get; private set; }

    public event EventHandler<DownloadProgress>? ProgressChanged;

    public event EventHandler<JobStatus>? StateChanged;

    public Task<ProbeResult> ProbeAsync(
        Uri url,
        RequestOptions options,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _probeCount);
        LastProbeOptions = options;

        return OnProbe?.Invoke(url, cancellationToken)
               ?? Task.FromResult(new ProbeResult(
                   url,
                   url,
                   4096,
                   true,
                   "\"etag\"",
                   null,
                   "hello.bin",
                   "application/octet-stream"));
    }

    public Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _downloadCount);
        LastDownloadRequest = request;

        return OnDownload?.Invoke(request, cancellationToken)
               ?? Task.FromResult(new DownloadResult(
                   request.TargetPath ?? string.Empty,
                   4096,
                   DownloadMode.Segmented,
                   TimeSpan.Zero));
    }

    public Task<DownloadResult> ResumeAsync(
        string stateOrWorkingPath,
        ResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _resumeCount);

        return OnResume?.Invoke(stateOrWorkingPath, options, cancellationToken)
               ?? Task.FromResult(new DownloadResult(
                   stateOrWorkingPath.EndsWith(".download.json", StringComparison.OrdinalIgnoreCase)
                       ? stateOrWorkingPath[..^".download.json".Length]
                       : stateOrWorkingPath,
                   4096,
                   DownloadMode.Segmented,
                   TimeSpan.Zero));
    }

    public void RaiseProgress(long bytes, long total) =>
        ProgressChanged?.Invoke(
            this,
            new DownloadProgress(bytes, total, 1024, TimeSpan.FromSeconds(1), 2));

    public void RaiseStateChanged(JobStatus status) =>
        StateChanged?.Invoke(this, status);
}
