using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Transfer;

namespace PotatoDM.AppCore.Services;

/// <summary>Creates engine instances so tests can substitute a fake.</summary>
public interface IDownloadEngineFactory
{
    IDownloadEngine Create();
}

/// <summary>
/// The production factory. Carries the app's log sink so engine-level entries
/// (snapshot write failures, subscriber faults) land in the same file as the
/// manager's, instead of vanishing into the no-op sink.
/// </summary>
public sealed class DownloadEngineFactory : IDownloadEngineFactory
{
    private readonly ILogSink _log;

    /// <param name="log">Optional sink; defaults to <see cref="NullLogSink"/>.</param>
    public DownloadEngineFactory(ILogSink? log = null)
    {
        _log = log ?? NullLogSink.Instance;
    }

    public IDownloadEngine Create() => new DownloadEngine(log: _log);
}
