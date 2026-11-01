using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Dht;

namespace PotatoDM.Torrent;

/// <summary>
/// Wraps MonoTorrent's DHT engine so it bootstraps from several public
/// routers. MonoTorrent 3.x only tries router.bittorrent.com, which is
/// unreachable on some networks; without a working DHT a trackerless magnet
/// can never find its first peer.
/// </summary>
internal sealed class BootstrapDhtEngine : IDhtEngine
{
    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);
    private static ReadOnlyMemory<byte>[]? bootstrapNodes;

    internal static readonly (string Host, int Port)[] Routers =
    {
        ("dht.transmissionbt.com", 6881),
        ("dht.libtorrent.org", 25401),
        ("router.bittorrent.com", 6881),
        ("router.utorrent.com", 6881),
        ("dht.aelitis.com", 6881),
    };

    private readonly DhtEngine _inner = new();

    public BootstrapDhtEngine()
    {
        _inner.PeersFound += (_, e) => PeersFound?.Invoke(this, e);
        _inner.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
    }

    public event EventHandler<PeersFoundEventArgs>? PeersFound;

    public event EventHandler? StateChanged;

    public TimeSpan AnnounceInterval => _inner.AnnounceInterval;

    public bool Disposed => _inner.Disposed;

    public ITransferMonitor Monitor => _inner.Monitor;

    public TimeSpan MinimumAnnounceInterval => _inner.MinimumAnnounceInterval;

    public int NodeCount => _inner.NodeCount;

    public DhtState State => _inner.State;

    public void Add(IEnumerable<ReadOnlyMemory<byte>> nodes) => _inner.Add(nodes);

    public void Announce(InfoHash infoHash, int port) => _inner.Announce(infoHash, port);

    public void Dispose() => _inner.Dispose();

    public void GetPeers(InfoHash infoHash) => _inner.GetPeers(infoHash);

    public Task<ReadOnlyMemory<byte>> SaveNodesAsync() => _inner.SaveNodesAsync();

    public Task SetListenerAsync(MonoTorrent.Connections.Dht.IDhtListener listener)
        => _inner.SetListenerAsync(listener);

    public Task StartAsync() => StartAsync(ReadOnlyMemory<byte>.Empty);

    public async Task StartAsync(ReadOnlyMemory<byte> initialNodes)
    {
        // Queued nodes are merged with the cached routing table by DhtEngine.
        var nodes = await GetBootstrapNodesAsync().ConfigureAwait(false);
        if (nodes.Length > 0)
        {
            _inner.Add(nodes);
        }

        await _inner.StartAsync(initialNodes).ConfigureAwait(false);
    }

    public Task StopAsync() => _inner.StopAsync();

    private static async Task<ReadOnlyMemory<byte>[]> GetBootstrapNodesAsync()
    {
        var cached = Volatile.Read(ref bootstrapNodes);
        if (cached is { Length: > 0 })
        {
            return cached;
        }

        await BootstrapLock.WaitAsync().ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref bootstrapNodes);
            if (cached is { Length: > 0 })
            {
                return cached;
            }

            var resolved = await ResolveBootstrapNodesAsync().ConfigureAwait(false);

            // Do not cache a failed lookup: the app may have started before
            // the network was available, and the next download should retry.
            if (resolved.Length > 0)
            {
                Volatile.Write(ref bootstrapNodes, resolved);
            }

            return resolved;
        }
        finally
        {
            BootstrapLock.Release();
        }
    }

    private static async Task<ReadOnlyMemory<byte>[]> ResolveBootstrapNodesAsync()
    {
        var tasks = Routers.Select(async router =>
        {
            try
            {
                var addresses = await Dns
                    .GetHostAddressesAsync(router.Host)
                    .WaitAsync(TimeSpan.FromSeconds(3))
                    .ConfigureAwait(false);

                return addresses
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => CreateCompactNode(address, router.Port))
                    .ToArray();
            }
            catch
            {
                // DNS failure is expected when a router is blocked; the
                // remaining routers still get a chance to bootstrap.
                return Array.Empty<ReadOnlyMemory<byte>>();
            }
        });

        var resolved = await Task.WhenAll(tasks).ConfigureAwait(false);
        return resolved.SelectMany(nodes => nodes).ToArray();
    }

    private static ReadOnlyMemory<byte> CreateCompactNode(IPAddress address, int port)
    {
        // BEP 5 compact node info: 20-byte id, 4-byte IPv4, 2-byte port.
        var node = new byte[26];
        Random.Shared.NextBytes(node.AsSpan(0, 20));
        address.GetAddressBytes().CopyTo(node, 20);
        node[24] = (byte)(port >> 8);
        node[25] = (byte)port;
        return node;
    }
}
