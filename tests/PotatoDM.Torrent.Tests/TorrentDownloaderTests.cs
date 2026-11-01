using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using PotatoDM.Torrent;
using BitTorrent = MonoTorrent.Torrent;

namespace PotatoDM.Torrent.Tests;

/// <summary>
/// End-to-end tests over loopback: one engine seeds a locally created torrent,
/// a second one (the product's downloader) fetches it. No internet is used.
/// </summary>
public class TorrentDownloaderTests
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task Downloads_a_torrent_file_from_a_loopback_seed()
    {
        using var temp = new TempDirectory();
        var payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);
        var torrentPath = await CreateTorrentFileAsync(temp, payload, "payload.bin", "seed");

        var port = GetFreeLoopbackPort();
        using var seeder = await StartSeederAsync(
            temp.FileFor("seed"),
            BitTorrent.Load(await File.ReadAllBytesAsync(torrentPath)),
            port,
            temp.FileFor("seeder-cache"));

        var result = await DownloadAsync(
            new TorrentDownloadRequest
            {
                Source = torrentPath,
                DownloadDirectory = temp.FileFor("download"),
                EnableDht = false,
                EnablePeerExchange = false,
                AddFallbackTrackers = false,
                PeerEndPoints = new[] { $"ipv4://127.0.0.1:{port}" },
            });

        Assert.False(result.IsDirectory);
        Assert.True(File.Exists(result.TargetPath), result.TargetPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.TargetPath));
    }

    [Fact]
    public async Task Downloads_a_magnet_link_and_fetches_metadata_over_the_wire()
    {
        using var temp = new TempDirectory();
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        var torrentPath = await CreateTorrentFileAsync(temp, payload, "video.bin", "seed");

        var torrent = BitTorrent.Load(await File.ReadAllBytesAsync(torrentPath));
        var port = GetFreeLoopbackPort();
        using var seeder = await StartSeederAsync(
            temp.FileFor("seed"),
            torrent,
            port,
            temp.FileFor("seeder-cache"));

        var magnet = new MagnetLink(
            torrent.InfoHashes,
            torrent.Name,
            new List<string>(),
            Array.Empty<string>(),
            torrent.Size).ToV1String();

        Assert.StartsWith("magnet:?", magnet);

        var result = await DownloadAsync(
            new TorrentDownloadRequest
            {
                Source = magnet,
                DownloadDirectory = temp.FileFor("download"),
                EnableDht = false,
                EnablePeerExchange = false,
                AddFallbackTrackers = false,
                PeerEndPoints = new[] { $"ipv4://127.0.0.1:{port}" },
            });

        Assert.True(File.Exists(result.TargetPath), result.TargetPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.TargetPath));
    }

    private static async Task<TorrentDownloadResult> DownloadAsync(TorrentDownloadRequest request)
    {
        using var timeout = new CancellationTokenSource(DownloadTimeout);

        return await new TorrentDownloader().DownloadAsync(request, null, timeout.Token);
    }

    private static async Task<string> CreateTorrentFileAsync(
        TempDirectory temp,
        byte[] payload,
        string fileName,
        string seedFolder)
    {
        var seedDirectory = temp.FileFor(seedFolder);
        Directory.CreateDirectory(seedDirectory);

        var payloadPath = Path.Combine(seedDirectory, fileName);
        await File.WriteAllBytesAsync(payloadPath, payload);

        var dictionary = await new TorrentCreator()
            .CreateAsync(new TorrentFileSource(payloadPath));
        var torrentPath = temp.FileFor(fileName + ".torrent");
        await File.WriteAllBytesAsync(torrentPath, dictionary.Encode());

        return torrentPath;
    }

    private static async Task<ClientEngine> StartSeederAsync(
        string seedDirectory,
        BitTorrent torrent,
        int port,
        string cacheDirectory)
    {
        var engineSettings = new EngineSettingsBuilder
        {
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Loopback, port),
            },
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            CacheDirectory = cacheDirectory,
            AutoSaveLoadFastResume = false,
            AutoSaveLoadMagnetLinkMetadata = false,
        }.ToSettings();

        var torrentSettings = new TorrentSettingsBuilder
        {
            AllowDht = false,
            AllowPeerExchange = false,
            CreateContainingDirectory = true,
        }.ToSettings();

        var engine = new ClientEngine(engineSettings);

        try
        {
            var manager = await engine.AddAsync(torrent, seedDirectory, torrentSettings);
            await manager.StartAsync();
            await WaitForStateAsync(manager, TorrentState.Seeding, TimeSpan.FromSeconds(30));
            return engine;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private static async Task WaitForStateAsync(
        TorrentManager manager,
        TorrentState expected,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (manager.State != expected)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Seeder did not reach {expected}; current state is {manager.State}.");
            }

            await Task.Delay(50);
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
