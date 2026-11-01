using System.Net;
using MonoTorrent;
using MonoTorrent.Client;
using LibtorrentClient = csdl.TorrentClient;
using LibtorrentClientConfig = csdl.TorrentClientConfig;
using LibtorrentInfo = csdl.TorrentInfo;
using LibtorrentManager = csdl.TorrentManager;
using LibtorrentState = csdl.Enums.TorrentState;
using LibtorrentStatus = csdl.TorrentStatus;
using SettingsPack = csdl.SettingsPack;

namespace PotatoDM.Torrent;

/// <summary>
/// Downloads a magnet link or .torrent through libtorrent (csdl). libtorrent
/// provides uTP, protocol encryption, DHT and peer exchange, which is what a
/// BitComet-class client needs. csdl cannot be handed a magnet, so MonoTorrent
/// is kept only to perform the metadata exchange; the resulting .torrent is
/// then transferred by libtorrent.
/// </summary>
public static class TorrentDownloader
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public static async Task<TorrentDownloadResult> DownloadAsync(
        TorrentDownloadRequest request,
        Action<TorrentProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Source))
        {
            throw new ArgumentException("A magnet link or .torrent path is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DownloadDirectory))
        {
            throw new ArgumentException("A download directory is required.", nameof(request));
        }

        Directory.CreateDirectory(request.DownloadDirectory);

        var cacheDirectory = string.IsNullOrWhiteSpace(request.ProcessDirectory)
            ? Path.Combine(request.DownloadDirectory, ".potatodm-torrent-cache")
            : Path.Combine(request.ProcessDirectory, "torrent-cache");

        // A caller which pins specific peers needs MonoTorrent's AddPeerAsync;
        // csdl has no equivalent API. The UI never uses this path.
        if (request.PeerEndPoints.Count > 0)
        {
            return await DownloadWithMonoTorrentAsync(
                    request,
                    cacheDirectory,
                    onProgress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var torrentBytes = await LoadTorrentAsync(
                request,
                cacheDirectory,
                onProgress,
                cancellationToken)
            .ConfigureAwait(false);

        torrentBytes = TorrentMetadataFile.WithFallbackTrackers(
            torrentBytes,
            request.AddFallbackTrackers);

        return await DownloadWithLibtorrentAsync(
                request,
                torrentBytes,
                onProgress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]> LoadTorrentAsync(
        TorrentDownloadRequest request,
        string cacheDirectory,
        Action<TorrentProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        if (!request.Source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(request.Source);

            if (!File.Exists(path))
            {
                throw new TorrentException($"Torrent file not found: {path}");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

            try
            {
                MonoTorrent.Torrent.Load(bytes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new TorrentException($"Cannot load torrent '{request.Source}': {ex.Message}", ex);
            }

            return bytes;
        }

        MagnetLink magnet;

        try
        {
            magnet = MagnetLink.Parse(request.Source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TorrentException($"Invalid magnet link: {ex.Message}", ex);
        }

        return await MagnetMetadataResolver
            .ResolveAsync(magnet, cacheDirectory, request, onProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<TorrentDownloadResult> DownloadWithLibtorrentAsync(
        TorrentDownloadRequest request,
        byte[] torrentBytes,
        Action<TorrentProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        LibtorrentInfo info;

        try
        {
            info = new LibtorrentInfo(torrentBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TorrentException($"Cannot read torrent metadata: {ex.Message}", ex);
        }

        var files = info.Files.Where(file => !file.IsPadFile).ToArray();

        if (files.Length == 0)
        {
            throw new TorrentException("The torrent does not contain any files.");
        }

        var totalBytes = files.Sum(file => file.FileSize);

        using var client = new LibtorrentClient(CreateLibtorrentSettings(request));

        LibtorrentManager manager;

        try
        {
            manager = client.AttachTorrent(info, request.DownloadDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TorrentException($"Cannot start the torrent: {ex.Message}", ex);
        }

        try
        {
            manager.Start();

            await WaitForLibtorrentAsync(manager, info, totalBytes, onProgress, cancellationToken)
                .ConfigureAwait(false);

            var result = DescribeLibtorrentResult(info, files, request.DownloadDirectory, totalBytes);

            if (request.SeedAfterDownload)
            {
                Report(info, manager.GetCurrentStatus(), totalBytes, onProgress);

                // Seeding holds the session open until the caller cancels.
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            return result;
        }
        finally
        {
            try
            {
                manager.Stop();
            }
            catch
            {
                // Disposing the client detaches the torrent regardless.
            }
        }
    }

    private static SettingsPack CreateLibtorrentSettings(TorrentDownloadRequest request)
    {
        var config = new LibtorrentClientConfig
        {
            UserAgent = "PotatoDM/1.0",
            MaxConnections = 200,
        };

        var settings = config.Build();
        var port = Math.Clamp(request.ListenPort, 0, 65535);

        // The plain interface form listens on both TCP and uTP/UDP.
        settings.Set("listen_interfaces", $"0.0.0.0:{port}");
        settings.Set("enable_dht", request.EnableDht);
        settings.Set("enable_lsd", false);
        settings.Set("enable_upnp", request.AllowPortForwarding);
        settings.Set("enable_natpmp", request.AllowPortForwarding);

        // Ask every tracker in every tier instead of stopping at the first
        // answer: stale link-site trackers often return an empty peer list.
        settings.Set("announce_to_all_tiers", true);
        settings.Set("announce_to_all_trackers", true);

        // 0 = forced, 1 = enabled, 2 = disabled. Enabled keeps plaintext peers
        // usable while allowing encrypted and uTP connections.
        settings.Set("in_enc_policy", 1);
        settings.Set("out_enc_policy", 1);

        // Only touch the rate limiters when the caller wants a cap. Explicitly
        // setting them to zero makes libtorrent's session replace its default
        // limiter with an idle one, which delays the first peer connection.
        if (request.MaximumDownloadBytesPerSecond > 0)
        {
            settings.Set("download_rate_limit", ToRate(request.MaximumDownloadBytesPerSecond));
        }

        if (request.MaximumUploadBytesPerSecond > 0)
        {
            settings.Set("upload_rate_limit", ToRate(request.MaximumUploadBytesPerSecond));
        }

        if (request.EnableDht)
        {
            settings.Set(
                "dht_bootstrap_nodes",
                string.Join(
                    ",",
                    BootstrapDhtEngine.Routers.Select(router => $"{router.Host}:{router.Port}")));
        }

        return settings;
    }

    private static async Task WaitForLibtorrentAsync(
        LibtorrentManager manager,
        LibtorrentInfo info,
        long totalBytes,
        Action<TorrentProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = manager.GetCurrentStatus();

            if (status.State == LibtorrentState.Errored)
            {
                throw new TorrentException("libtorrent reported an unrecoverable torrent error.");
            }

            Report(info, status, totalBytes, onProgress);

            // Finished covers a torrent whose files were already complete when
            // it was attached; Seeding covers a transfer that just finished.
            if (status.State is LibtorrentState.Seeding or LibtorrentState.Finished)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Report(
        LibtorrentInfo info,
        LibtorrentStatus status,
        long totalBytes,
        Action<TorrentProgress>? onProgress)
    {
        if (onProgress is null)
        {
            return;
        }

        var progress = Math.Clamp(status.Progress, 0f, 1f);
        var byProgress = (long)(totalBytes * progress);
        var downloaded = progress >= 1f
            ? totalBytes
            : Math.Clamp(
                Math.Max(byProgress, status.BytesDownloaded),
                0,
                totalBytes);

        onProgress(new TorrentProgress(
            info.Metadata.Name,
            totalBytes,
            downloaded,
            status.DownloadRate,
            status.UploadRate,
            // libtorrent's num_peers already includes the seeds among them.
            status.PeerCount,
            true,
            IsChecking(status.State)));
    }

    private static bool IsChecking(LibtorrentState state) =>
        state is LibtorrentState.CheckingFiles or LibtorrentState.CheckingResume;

    private static TorrentDownloadResult DescribeLibtorrentResult(
        LibtorrentInfo info,
        IReadOnlyList<csdl.TorrentFileInfo> files,
        string downloadDirectory,
        long totalBytes)
    {
        if (files.Count == 1)
        {
            var path = CombineRelative(downloadDirectory, files[0].Path);
            return new TorrentDownloadResult(path, false, totalBytes, info.Metadata.Name);
        }

        var directory = CombineRelative(downloadDirectory, info.Metadata.Name);
        return new TorrentDownloadResult(directory, true, totalBytes, info.Metadata.Name);
    }

    private static async Task<TorrentDownloadResult> DownloadWithMonoTorrentAsync(
        TorrentDownloadRequest request,
        string cacheDirectory,
        Action<TorrentProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var engineSettings = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory,
            AutoSaveLoadFastResume = true,
            FastResumeMode = FastResumeMode.Accurate,
            AllowPortForwarding = request.AllowPortForwarding,
            AllowLocalPeerDiscovery = false,
            DhtEndPoint = request.EnableDht ? new IPEndPoint(IPAddress.Any, 0) : null,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Any, Math.Clamp(request.ListenPort, 0, 65535)),
            },
        }.ToSettings();

        var torrentSettings = new TorrentSettingsBuilder
        {
            AllowDht = request.EnableDht,
            AllowPeerExchange = request.EnablePeerExchange,
            MaximumDownloadRate = ToRate(request.MaximumDownloadBytesPerSecond),
            MaximumUploadRate = ToRate(request.MaximumUploadBytesPerSecond),
            CreateContainingDirectory = true,
        }.ToSettings();

        var factories = request.EnableDht
            ? Factories.Default.WithDhtCreator(() => new BootstrapDhtEngine())
            : Factories.Default;

        using var engine = new ClientEngine(engineSettings, factories);

        try
        {
            var manager = await AddWithMonoTorrentAsync(
                    engine,
                    request.Source,
                    request.DownloadDirectory,
                    torrentSettings,
                    cancellationToken)
                .ConfigureAwait(false);

            if (request.AddFallbackTrackers)
            {
                await TrackerFallback.AddAsync(manager).ConfigureAwait(false);
            }

            await manager.StartAsync().ConfigureAwait(false);

            foreach (var endpoint in request.PeerEndPoints)
            {
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var peerUri))
                {
                    throw new TorrentException($"Invalid peer endpoint: {endpoint}");
                }

                await manager.AddPeerAsync(new PeerInfo(peerUri)).ConfigureAwait(false);
            }

            await WaitForMonoTorrentAsync(manager, request, onProgress, cancellationToken)
                .ConfigureAwait(false);

            var result = DescribeMonoTorrentResult(manager, request.DownloadDirectory);

            if (request.SeedAfterDownload)
            {
                ReportMonoTorrent(manager, onProgress);

                // Seeding holds the engine open until the caller cancels.
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            else
            {
                await manager.StopAsync().ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            await StopBestEffortAsync(engine).ConfigureAwait(false);
        }
    }

    private static async Task<TorrentManager> AddWithMonoTorrentAsync(
        ClientEngine engine,
        string source,
        string downloadDirectory,
        TorrentSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                var magnet = MagnetLink.Parse(source);
                return await engine
                    .AddAsync(magnet, downloadDirectory, settings)
                    .ConfigureAwait(false);
            }

            var path = Path.GetFullPath(source);
            if (!File.Exists(path))
            {
                throw new TorrentException($"Torrent file not found: {path}");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var torrent = MonoTorrent.Torrent.Load(bytes);
            return await engine
                .AddAsync(torrent, downloadDirectory, settings)
                .ConfigureAwait(false);
        }
        catch (TorrentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TorrentException($"Cannot load torrent '{source}': {ex.Message}", ex);
        }
    }

    private static async Task WaitForMonoTorrentAsync(
        TorrentManager manager,
        TorrentDownloadRequest request,
        Action<TorrentProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        while (!manager.Complete)
        {
            if (manager.State == TorrentState.Error)
            {
                throw new TorrentException("MonoTorrent reported an unrecoverable torrent error.");
            }

            ReportMonoTorrent(manager, onProgress);
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        ReportMonoTorrent(manager, onProgress);
    }

    private static void ReportMonoTorrent(
        TorrentManager manager,
        Action<TorrentProgress>? onProgress)
    {
        if (onProgress is null)
        {
            return;
        }

        // Before metadata arrives a magnet may have no dn= name. Report an
        // empty name rather than a placeholder so the UI keeps its own label.
        var name = manager.Torrent?.Name
                   ?? manager.MagnetLink?.Name
                   ?? string.Empty;
        var total = manager.Torrent?.Size ?? 0;
        var percent = total > 0
            ? manager.Progress
            : 0;
        var downloaded = total > 0
            ? (long)(total * Math.Clamp(percent, 0, 100) / 100.0)
            : 0;

        onProgress(new TorrentProgress(
            name,
            total,
            downloaded,
            manager.Monitor.DownloadRate,
            manager.Monitor.UploadRate,
            manager.OpenConnections,
            manager.HasMetadata));
    }

    private static TorrentDownloadResult DescribeMonoTorrentResult(
        TorrentManager manager,
        string downloadDirectory)
    {
        var torrent = manager.Torrent
                      ?? throw new TorrentException("Torrent metadata was never received.");

        if (torrent.Files.Count == 1)
        {
            var file = torrent.Files[0];
            var path = CombineRelative(downloadDirectory, file.Path);
            return new TorrentDownloadResult(path, false, torrent.Size, torrent.Name);
        }

        var directory = CombineRelative(downloadDirectory, torrent.Name);
        return new TorrentDownloadResult(directory, true, torrent.Size, torrent.Name);
    }

    private static string CombineRelative(string root, string relative)
    {
        var parts = relative
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            if (part is "." or "..")
            {
                throw new TorrentException("The torrent contains an unsafe relative path.");
            }
        }

        var combined = Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
        var fullRoot = Path.GetFullPath(root);

        if (!combined.StartsWith(
                fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new TorrentException("The torrent writes outside the download directory.");
        }

        return combined;
    }

    private static int ToRate(long bytesPerSecond) =>
        bytesPerSecond <= 0 ? 0 : (int)Math.Min(bytesPerSecond, int.MaxValue);

    private static async Task StopBestEffortAsync(ClientEngine engine)
    {
        try
        {
            await engine.StopAllAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
