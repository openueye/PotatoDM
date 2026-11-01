using PotatoDM.Core.Transfer;

namespace PotatoDM.AppCore.Services;

/// <summary>Creates engine instances so tests can substitute a fake.</summary>
public interface IDownloadEngineFactory
{
    IDownloadEngine Create();
}

public sealed class DownloadEngineFactory : IDownloadEngineFactory
{
    public IDownloadEngine Create() => new DownloadEngine();
}
