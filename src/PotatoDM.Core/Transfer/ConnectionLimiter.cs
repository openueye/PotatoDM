using System.Collections.Concurrent;

namespace PotatoDM.Core.Transfer;

/// <summary>Global plus per-authority connection caps.</summary>
public sealed class ConnectionLimiter : IDisposable
{
    private readonly SemaphoreSlim _global;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perHost =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly int _perHostLimit;
    private int _disposed;

    public ConnectionLimiter(int maxGlobal, int maxPerHost)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGlobal, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerHost, 1);

        _global = new SemaphoreSlim(maxGlobal, maxGlobal);
        _perHostLimit = maxPerHost;
    }

    public async ValueTask<Lease> AcquireAsync(Uri uri, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _global.WaitAsync(cancellationToken).ConfigureAwait(false);
        var hostKey = uri.GetLeftPart(UriPartial.Authority);
        var hostSemaphore = _perHost.GetOrAdd(
            hostKey,
            _ => new SemaphoreSlim(_perHostLimit, _perHostLimit));

        try
        {
            await hostSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _global.Release();
            throw;
        }

        return new Lease(_global, hostSemaphore);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _global.Dispose();
        foreach (var semaphore in _perHost.Values)
        {
            semaphore.Dispose();
        }

        _perHost.Clear();
    }

    public sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _global;
        private SemaphoreSlim? _host;

        internal Lease(SemaphoreSlim global, SemaphoreSlim host)
        {
            _global = global;
            _host = host;
        }

        public void Dispose()
        {
            var host = Interlocked.Exchange(ref _host, null);
            if (host is null)
            {
                return;
            }

            host.Release();
            Interlocked.Exchange(ref _global, null)?.Release();
        }
    }
}
