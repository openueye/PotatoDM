using System.Globalization;
using System.Net;
using MonoTorrent;
using MonoTorrent.Client;

namespace PotatoDM.Torrent;

/// <summary>
/// Gets the .torrent metadata for a magnet link. libtorrent cannot be handed a
/// magnet through csdl, so the metadata exchange is done by MonoTorrent (or an
/// HTTP metadata cache) and the verified .torrent is then used for the transfer.
/// </summary>
internal static class MagnetMetadataResolver
{
    private const int MaximumMetadataBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // Public metadata caches. They only ever receive the info hash, and their
    // answer is accepted only when it matches that hash.
    private static readonly string[] MetadataCaches =
    {
        "https://itorrents.org/torrent/{0}.torrent",
        "https://itorrents.net/torrent/{0}.torrent",
    };

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static async Task<byte[]> ResolveAsync(
        MagnetLink magnet,
        string cacheDirectory,
        TorrentDownloadRequest request,
        Action<TorrentProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var metadataDirectory = Path.Combine(cacheDirectory, "metadata");
        var metadataPath = Path.Combine(
            metadataDirectory,
            magnet.InfoHashes.V1OrV2.ToHex() + ".torrent");

        if (TorrentMetadataFile.TryRead(metadataPath, magnet.InfoHashes, out var cached))
        {
            return cached;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var handedOff = false;

        try
        {
            var pending = new List<Task<byte[]>>
            {
                TryHttpAsync(magnet, metadataPath, linked.Token),
                TryPeerWireAsync(magnet, cacheDirectory, request, onProgress, linked.Token),
            };

            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);

                if (finished.IsCompletedSuccessfully)
                {
                    linked.Cancel();

                    // Do not wait for the losing attempt. MonoTorrent can take
                    // a minute to tear down its metadata engine, and the caller
                    // already has valid metadata. The continuation only
                    // observes the cancellation and releases the token source.
                    handedOff = true;
                    _ = ObserveFailuresAsync(pending, linked);
                    return finished.Result;
                }

                await ObserveFailureAsync(finished).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (!handedOff)
            {
                linked.Dispose();
            }
        }

        throw new TorrentException(
            "Could not retrieve the torrent metadata. The swarm may be offline, " +
            "or the magnet may only accept uTP/encrypted peers.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PotatoDM/1.0");
        return client;
    }

    private static async Task<byte[]> TryHttpAsync(
        MagnetLink magnet,
        string metadataPath,
        CancellationToken cancellationToken)
    {
        var hex = magnet.InfoHashes.V1OrV2.ToHex();

        foreach (var template in MetadataCaches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var url = string.Format(CultureInfo.InvariantCulture, template, hex);
                using var response = await HttpClient
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode ||
                    response.Content.Headers.ContentLength > MaximumMetadataBytes)
                {
                    continue;
                }

                var bytes = await response.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (bytes.Length is 0 or > MaximumMetadataBytes ||
                    !TorrentMetadataFile.TryValidate(bytes, magnet.InfoHashes, out var valid))
                {
                    continue;
                }

                await TorrentMetadataFile.WriteCacheAsync(metadataPath, valid, cancellationToken)
                    .ConfigureAwait(false);
                return valid;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                // Try the next cache; the peer-wire attempt keeps running.
            }
        }

        throw new TorrentException("No HTTP metadata cache returned a matching .torrent.");
    }

    private static async Task<byte[]> TryPeerWireAsync(
        MagnetLink magnet,
        string cacheDirectory,
        TorrentDownloadRequest request,
        Action<TorrentProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var engineSettings = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory,
            AutoSaveLoadFastResume = false,
            AutoSaveLoadMagnetLinkMetadata = true,
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            DhtEndPoint = request.EnableDht ? new IPEndPoint(IPAddress.Any, 0) : null,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Any, Math.Clamp(request.ListenPort, 0, 65535)),
            },
        }.ToSettings();

        var factories = request.EnableDht
            ? Factories.Default.WithDhtCreator(() => new BootstrapDhtEngine())
            : Factories.Default;

        using var engine = new ClientEngine(engineSettings, factories);

        var torrentSettings = new TorrentSettingsBuilder
        {
            AllowDht = request.EnableDht,
            AllowPeerExchange = request.EnablePeerExchange,
            CreateContainingDirectory = true,
        }.ToSettings();

        var manager = await engine
            .AddAsync(magnet, request.DownloadDirectory, torrentSettings)
            .ConfigureAwait(false);

        if (request.AddFallbackTrackers)
        {
            await TrackerFallback.AddAsync(manager).ConfigureAwait(false);
        }

        await manager.StartAsync().ConfigureAwait(false);

        try
        {
            while (!manager.HasMetadata)
            {
                Report(magnet, manager, onProgress);
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await manager.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // The engine is disposed below; stopping is best effort.
            }
        }

        // MetadataMode writes the .torrent before it publishes the metadata.
        if (!TorrentMetadataFile.TryRead(manager.MetadataPath, magnet.InfoHashes, out var bytes))
        {
            throw new TorrentException("The metadata received from the swarm was not valid.");
        }

        return bytes;
    }

    private static void Report(
        MagnetLink magnet,
        TorrentManager manager,
        Action<TorrentProgress>? onProgress)
    {
        if (onProgress is null)
        {
            return;
        }

        var name = manager.Torrent?.Name ?? magnet.Name ?? string.Empty;

        onProgress(new TorrentProgress(
            name,
            0,
            0,
            manager.Monitor.DownloadRate,
            manager.Monitor.UploadRate,
            manager.OpenConnections,
            manager.HasMetadata));
    }

    private static async Task ObserveFailuresAsync(
        IEnumerable<Task> tasks,
        CancellationTokenSource linked)
    {
        try
        {
            foreach (var task in tasks)
            {
                await ObserveFailureAsync(task).ConfigureAwait(false);
            }
        }
        finally
        {
            linked.Dispose();
        }
    }

    private static async Task ObserveFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The losing attempt was cancelled or failed; the winner is used.
        }
    }
}
