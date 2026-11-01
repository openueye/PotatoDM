using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Http;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;
using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Media;

/// <summary>
/// Downloads an HLS or DASH manifest by fetching every media segment, decrypting
/// ordinary AES-128 HLS segments, and concatenating the result. Separate audio
/// and video tracks are muxed with ffmpeg when it is available; DRM-protected
/// manifests are refused before any media request is made.
/// </summary>
public sealed class MediaDownloader
{
    /// <summary>Cache directory suffix used beside the target file.</summary>
    public const string CacheSuffix = ".potatodm-media";

    private const int BufferSize = 81920;
    private const long MaxManifestBytes = 32L * 1024 * 1024;
    private const long MaxKeyBytes = 1024 * 1024;

    public async Task<DownloadResult> DownloadAsync(
        HttpClient client,
        DownloadRequest request,
        ManifestKind kind,
        ProbeResult probe,
        Action<DownloadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(probe);

        var stopwatch = Stopwatch.StartNew();
        var requestOptions = request.RequestOptions;
        var retry = new RetryPolicy(request.MaxRetries, request.MaxRetryDelay);
        var manifestUri = probe.FinalUri;

        var plan = kind == ManifestKind.Hls
            ? await BuildHlsPlanAsync(client, manifestUri, requestOptions, retry, cancellationToken)
                .ConfigureAwait(false)
            : await BuildDashPlanAsync(client, manifestUri, requestOptions, retry, cancellationToken)
                .ConfigureAwait(false);

        if (plan.Tracks.Count == 0)
        {
            throw new MediaManifestException("The manifest contains no downloadable audio or video track.");
        }

        var targetPath = ResolveTarget(request.TargetPath, probe, plan.ContainerExtension);

        var hasVideo = plan.Tracks.Any(track =>
            track.Kind.Equals("video", StringComparison.OrdinalIgnoreCase));
        var hasAudio = plan.Tracks.Any(track =>
            track.Kind.Equals("audio", StringComparison.OrdinalIgnoreCase));
        var muxed = hasVideo && hasAudio && plan.Tracks.Count > 1;
        string? ffmpegPath = null;

        if (muxed)
        {
            ffmpegPath = FfmpegMuxer.FindFfmpeg(request.FfmpegPath);
            if (ffmpegPath is null)
            {
                throw new MediaMergeException(
                    "This manifest stores audio and video separately, so merging needs ffmpeg. " +
                    "Install ffmpeg and put it on PATH, or pass its location with --ffmpeg.");
            }
        }

        var processDirectory = string.IsNullOrWhiteSpace(request.ProcessDirectory)
            ? null
            : Path.GetFullPath(request.ProcessDirectory);
        var cacheRoot = processDirectory is null
            ? targetPath + CacheSuffix
            : Path.Combine(processDirectory, StatePaths.MediaCacheDirectoryName);
        PrepareCache(cacheRoot, plan.Fingerprint, request.Restart);

        using var limiter = new ConnectionLimiter(
            request.MaxGlobalConnections,
            request.MaxConnectionsPerHost);

        var progress = new ProgressAggregator(onProgress);
        var keyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var cached = new List<CachedTrack>();

        try
        {
            foreach (var track in plan.Tracks)
            {
                cached.Add(PrepareTrackCache(track, cacheRoot));
            }

            await FetchKeysAsync(client, plan, requestOptions, retry, keyCache, cancellationToken)
                .ConfigureAwait(false);

            await FetchAllPartsAsync(
                    client,
                    cached,
                    requestOptions,
                    retry,
                    limiter,
                    keyCache,
                    request.BandwidthLimiter,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            var target = muxed
                ? await MergeWithFfmpegAsync(
                        cached,
                        targetPath,
                        plan.ContainerExtension,
                        ffmpegPath!,
                        processDirectory,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await ConcatenateAsync(cached[0], targetPath, processDirectory, cancellationToken)
                    .ConfigureAwait(false);

            DeleteCacheBestEffort(cacheRoot);
            stopwatch.Stop();

            return new DownloadResult(
                target,
                new FileInfo(target).Length,
                plan.Tracks.Sum(track => track.Segments.Count) > 1
                    ? DownloadMode.Segmented
                    : DownloadMode.SingleStream,
                stopwatch.Elapsed);
        }
        catch
        {
            // Keep the segment cache: a retry can reuse every completed part.
            throw;
        }
    }

    private static string ResolveTarget(string? requestedPath, ProbeResult probe, string extension)
    {
        var baseName = MediaNaming.StripManifestExtension(probe.SuggestedFileName)
                       ?? MediaNaming.BaseNameFromUrl(probe.FinalUri);
        var suggested = FileNameSanitizer.Sanitize(baseName + extension);

        var target = TargetPathResolver.Resolve(requestedPath, suggested);
        var existingExtension = Path.GetExtension(target);

        if (string.IsNullOrEmpty(existingExtension))
        {
            return TargetPathResolver.Resolve(target + extension, null);
        }

        // A manifest URL or a provisional .mp4 guess must not label MPEG-TS
        // bytes as an MP4. Replace only known media/manifest extensions so an
        // explicit unusual name (e.g. .bin) is still honoured.
        if (IsMediaExtension(existingExtension) &&
            !existingExtension.Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            var corrected = Path.ChangeExtension(target, extension);
            return TargetPathResolver.Resolve(corrected, null);
        }

        return target;
    }

    private static bool IsMediaExtension(string extension) =>
        extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mpd", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".m4s", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    private async Task<MediaSourcePlan> BuildHlsPlanAsync(
        HttpClient client,
        Uri manifestUri,
        RequestOptions requestOptions,
        RetryPolicy retry,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveHlsMediaAsync(
                client,
                manifestUri,
                requestOptions,
                retry,
                selectAudio: true,
                depth: 0,
                cancellationToken)
            .ConfigureAwait(false);

        var video = resolved.Playlist;
        EnsureVod(video, resolved.MediaUri);

        var videoTrack = HlsTrack("video", "video", video);
        var texts = new StringBuilder(resolved.Text);

        if (resolved.Audio is { Uri: { } audioUri })
        {
            var audioResolved = await ResolveHlsMediaAsync(
                    client,
                    audioUri,
                    requestOptions,
                    retry,
                    selectAudio: false,
                    depth: 0,
                    cancellationToken)
                .ConfigureAwait(false);

            EnsureVod(audioResolved.Playlist, audioResolved.MediaUri);
            texts.Append('\n').Append(audioResolved.Text);

            var audioTrack = HlsTrack("audio", "audio", audioResolved.Playlist);
            audioTrack = WithLanguage(audioTrack, resolved.Audio.Language);

            var extension = ChooseMuxedExtension(videoTrack.ContainerExtension, audioTrack.ContainerExtension);

            return new MediaSourcePlan
            {
                Kind = ManifestKind.Hls,
                Tracks = new[] { videoTrack, audioTrack },
                ContainerExtension = extension,
                Warnings = Array.Empty<string>(),
                Fingerprint = Fingerprint(texts.ToString()),
            };
        }

        return new MediaSourcePlan
        {
            Kind = ManifestKind.Hls,
            Tracks = new[] { videoTrack },
            ContainerExtension = videoTrack.ContainerExtension,
            Warnings = Array.Empty<string>(),
            Fingerprint = Fingerprint(texts.ToString()),
        };
    }

    private async Task<MediaSourcePlan> BuildDashPlanAsync(
        HttpClient client,
        Uri manifestUri,
        RequestOptions requestOptions,
        RetryPolicy retry,
        CancellationToken cancellationToken)
    {
        var text = await FetchTextAsync(client, manifestUri, requestOptions, retry, cancellationToken)
            .ConfigureAwait(false);
        return DashManifestParser.Parse(text, manifestUri);
    }

    private async Task<HlsResolvedPlaylist> ResolveHlsMediaAsync(
        HttpClient client,
        Uri playlistUri,
        RequestOptions requestOptions,
        RetryPolicy retry,
        bool selectAudio,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 3)
        {
            throw new MediaManifestException("HLS playlists are nested too deeply.");
        }

        var text = await FetchTextAsync(client, playlistUri, requestOptions, retry, cancellationToken)
            .ConfigureAwait(false);

        if (!HlsPlaylistParser.IsMaster(text))
        {
            var playlist = HlsPlaylistParser.ParseMedia(text, playlistUri);
            return new HlsResolvedPlaylist(playlistUri, playlist, text, null);
        }

        var master = HlsPlaylistParser.ParseMaster(text, playlistUri);
        HlsRendition? audio = null;

        if (master.Variants.Count == 0)
        {
            // Audio-only masters list renditions instead of variants.
            var rendition = master.Renditions.FirstOrDefault(r => r.Uri is not null)
                            ?? throw new MediaManifestException(
                                "The HLS master playlist contains no playable variant.");
            var inner = await ResolveHlsMediaAsync(
                    client,
                    rendition.Uri!,
                    requestOptions,
                    retry,
                    selectAudio: false,
                    depth + 1,
                    cancellationToken)
                .ConfigureAwait(false);

            return inner with { Text = text + "\n" + inner.Text };
        }

        var variant = SelectVariant(master);

        if (selectAudio)
        {
            audio = SelectAudioRendition(master, variant);
        }

        var nested = await ResolveHlsMediaAsync(
                client,
                variant.Uri,
                requestOptions,
                retry,
                selectAudio: false,
                depth + 1,
                cancellationToken)
            .ConfigureAwait(false);

        return new HlsResolvedPlaylist(
            nested.MediaUri,
            nested.Playlist,
            text + "\n" + nested.Text,
            audio);
    }

    private static HlsVariant SelectVariant(HlsMasterPlaylist master)
    {
        var ordered = master.Variants
            .OrderByDescending(variant => variant.Bandwidth)
            .ThenByDescending(variant => ParseResolutionArea(variant.Resolution))
            .ToList();

        return ordered.Count == 0
            ? throw new MediaManifestException("The HLS master playlist contains no variants.")
            : ordered[0];
    }

    private static long ParseResolutionArea(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return 0;
        }

        var parts = resolution.Split('x', 'X');
        return parts.Length == 2 &&
               long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
               long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            ? width * height
            : 0;
    }

    private static HlsRendition? SelectAudioRendition(HlsMasterPlaylist master, HlsVariant variant)
    {
        if (string.IsNullOrWhiteSpace(variant.AudioGroup))
        {
            return null;
        }

        var candidates = master.Renditions
            .Where(rendition =>
                rendition.Uri is not null &&
                rendition.Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rendition.GroupId, variant.AudioGroup, StringComparison.Ordinal))
            .ToList();

        return candidates.FirstOrDefault(rendition => rendition.IsDefault) ?? candidates.FirstOrDefault();
    }

    private static void EnsureVod(HlsMediaPlaylist playlist, Uri playlistUri)
    {
        if (!playlist.EndList)
        {
            throw new MediaManifestException(
                $"The HLS playlist '{playlistUri}' has no #EXT-X-ENDLIST, so it is a live stream. " +
                "Live streams cannot be merged into one file.");
        }

        if (playlist.Segments.Count == 0)
        {
            throw new MediaManifestException($"The HLS playlist '{playlistUri}' contains no segments.");
        }
    }

    private static MediaTrackPlan HlsTrack(string kind, string id, HlsMediaPlaylist playlist)
    {
        var hasInitialization = playlist.Segments.Any(segment => segment.Initialization is not null);
        var extension = hasInitialization ? ".mp4" : ".ts";

        return new MediaTrackPlan
        {
            Kind = kind,
            Segments = playlist.Segments,
            ContainerExtension = extension,
        };
    }

    private static MediaTrackPlan WithLanguage(MediaTrackPlan track, string? language) => new()
    {
        Kind = track.Kind,
        Language = language ?? track.Language,
        Segments = track.Segments,
        ContainerExtension = track.ContainerExtension,
    };

    private static string ChooseMuxedExtension(string videoExtension, string audioExtension) =>
        videoExtension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
        audioExtension.StartsWith(".web", StringComparison.OrdinalIgnoreCase)
            ? ".webm"
            : ".mp4";

    private async Task FetchKeysAsync(
        HttpClient client,
        MediaSourcePlan plan,
        RequestOptions requestOptions,
        RetryPolicy retry,
        Dictionary<string, byte[]> keys,
        CancellationToken cancellationToken)
    {
        var keyUris = plan.Tracks
            .SelectMany(track => track.Segments)
            .Select(segment => segment.Key?.Uri)
            .Where(uri => uri is not null)
            .Select(uri => uri!)
            .Distinct();

        foreach (var keyUri in keyUris)
        {
            var key = await FetchKeyAsync(client, keyUri, requestOptions, retry, cancellationToken)
                .ConfigureAwait(false);
            keys[keyUri.AbsoluteUri] = key;
        }
    }

    private static async Task<byte[]> FetchKeyAsync(
        HttpClient client,
        Uri keyUri,
        RequestOptions requestOptions,
        RetryPolicy retry,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = DownloadRequestFactory.CreatePlainGet(keyUri, requestOptions);
                var (response, _) = await HttpSender.SendAsync(client, request, cancellationToken)
                    .ConfigureAwait(false);

                using (response)
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw CreateHttpException(response, keyUri);
                    }

                    EnsureIdentityEncoding(response);

                    if (response.Content.Headers.ContentLength is > MaxKeyBytes)
                    {
                        throw new MediaManifestException($"The HLS key at '{keyUri}' is unexpectedly large.");
                    }

                    var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (raw.Length > MaxKeyBytes)
                    {
                        throw new MediaManifestException($"The HLS key at '{keyUri}' is unexpectedly large.");
                    }

                    return NormalizeKey(raw, keyUri);
                }
            }
            catch (Exception ex) when (attempt < retry.MaxRetries && IsRetryable(ex, cancellationToken))
            {
                await Task.Delay(retry.GetDelay(attempt, null), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static byte[] NormalizeKey(byte[] raw, Uri keyUri)
    {
        if (raw.Length == 16)
        {
            return raw;
        }

        if (raw.Length is 32 or 34)
        {
            var text = Encoding.ASCII.GetString(raw).Trim();
            if (text.Length == 32)
            {
                try
                {
                    return Convert.FromHexString(text);
                }
                catch (FormatException)
                {
                }
            }
        }

        throw new MediaManifestException(
            $"The AES-128 key at '{keyUri}' is not 16 bytes (got {raw.Length}).");
    }

    private static CachedTrack PrepareTrackCache(MediaTrackPlan track, string cacheRoot)
    {
        var directory = Path.Combine(cacheRoot, SanitizeTrackName(track.Kind));
        Directory.CreateDirectory(directory);

        var inits = new Dictionary<MediaInitialization, CachedInitialization>();
        var segments = new List<CachedSegment>();
        var index = 0;

        foreach (var segment in track.Segments)
        {
            if (segment.Initialization is { } initialization &&
                !inits.ContainsKey(initialization))
            {
                inits[initialization] = new CachedInitialization(
                    initialization,
                    Path.Combine(directory, $"init-{inits.Count:D3}.bin"));
            }

            if (!segment.Gap)
            {
                segments.Add(new CachedSegment(
                    segment,
                    Path.Combine(directory, $"seg-{index:D6}.bin")));
            }

            index++;
        }

        return new CachedTrack(track, directory, inits.Values.ToList(), segments);
    }

    private static string SanitizeTrackName(string kind) =>
        kind.Equals("audio", StringComparison.OrdinalIgnoreCase) ? "audio" : "video";

    private static async Task FetchAllPartsAsync(
        HttpClient client,
        IReadOnlyList<CachedTrack> tracks,
        RequestOptions requestOptions,
        RetryPolicy retry,
        ConnectionLimiter limiter,
        Dictionary<string, byte[]> keys,
        IBandwidthLimiter? bandwidthLimiter,
        ProgressAggregator progress,
        CancellationToken cancellationToken)
    {
        var jobs = new List<FetchJob>();

        foreach (var track in tracks)
        {
            foreach (var initialization in track.Initializations)
            {
                if (!File.Exists(initialization.Path))
                {
                    jobs.Add(FetchJob.ForInitialization(initialization));
                }
            }

            foreach (var segment in track.Segments)
            {
                if (!File.Exists(segment.Path))
                {
                    jobs.Add(FetchJob.ForSegment(segment));
                }
            }
        }

        if (jobs.Count == 0)
        {
            return;
        }

        await Parallel.ForEachAsync(
                jobs,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Math.Min(16, jobs.Count)),
                    CancellationToken = cancellationToken,
                },
                async (job, token) =>
                {
                    using var lease = await limiter.AcquireAsync(job.Uri, token).ConfigureAwait(false);
                    progress.Enter();

                    try
                    {
                        await FetchPartWithRetryAsync(
                                client,
                                job,
                                requestOptions,
                                retry,
                                keys,
                                bandwidthLimiter,
                                progress,
                                token)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        progress.Leave();
                    }
                })
            .ConfigureAwait(false);
    }

    private static async Task FetchPartWithRetryAsync(
        HttpClient client,
        FetchJob job,
        RequestOptions requestOptions,
        RetryPolicy retry,
        Dictionary<string, byte[]> keys,
        IBandwidthLimiter? bandwidthLimiter,
        ProgressAggregator progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await FetchPartOnceAsync(
                        client,
                        job,
                        requestOptions,
                        keys,
                        bandwidthLimiter,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < retry.MaxRetries && IsRetryable(ex, cancellationToken))
            {
                var retryAfter = ex.Data.Contains("RetryAfter") &&
                                 ex.Data["RetryAfter"] is TimeSpan delay
                    ? delay
                    : (TimeSpan?)null;

                await Task.Delay(retry.GetDelay(attempt, retryAfter), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task FetchPartOnceAsync(
        HttpClient client,
        FetchJob job,
        RequestOptions requestOptions,
        Dictionary<string, byte[]> keys,
        IBandwidthLimiter? bandwidthLimiter,
        ProgressAggregator progress,
        CancellationToken cancellationToken)
    {
        var temporary = job.Path + ".tmp";

        try
        {
            using var request = DownloadRequestFactory.CreatePlainGet(job.Uri, requestOptions);

            if (job.Range is { } range)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                    range.Offset,
                    range.EndInclusive);
            }

            var (response, _) = await HttpSender.SendAsync(client, request, cancellationToken)
                .ConfigureAwait(false);

            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                {
                    throw CreateHttpException(response, job.Uri);
                }

                EnsureIdentityEncoding(response);

                await using var body = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                var skip = job.Range is { } requested && response.StatusCode == HttpStatusCode.OK
                    ? requested.Offset
                    : 0;
                var limit = job.Range?.Length ?? long.MaxValue;

                using var bounded = new BoundedReadStream(body, skip, limit);

                await using (var output = new FileStream(
                                 temporary,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 BufferSize,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var key = job.Key is { Uri: { } keyUri } ? keys[keyUri.AbsoluteUri] : null;

                    if (key is null)
                    {
                        await CopyWithProgressAsync(
                                bounded,
                                output,
                                bandwidthLimiter,
                                progress,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        var iv = job.Key!.Iv ?? Aes128Cbc.DeriveIv(job.Sequence);

                        using var aes = Aes.Create();
                        aes.Key = key;
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;

                        await using var decrypting = new CryptoStream(
                            bounded,
                            aes.CreateDecryptor(),
                            CryptoStreamMode.Read,
                            leaveOpen: true);

                        await CopyWithProgressAsync(
                                decrypting,
                                output,
                                bandwidthLimiter,
                                progress,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                if (job.Range is { } expected && bounded.TotalRead != expected.Length)
                {
                    throw new HttpDownloadException(
                        $"Media segment '{job.Uri}' returned {bounded.TotalRead} bytes " +
                        $"but {expected.Length} were requested.",
                        isTransient: true);
                }
            }

            File.Move(temporary, job.Path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        IBandwidthLimiter? bandwidthLimiter,
        ProgressAggregator progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

            if (bandwidthLimiter is not null)
            {
                await bandwidthLimiter.ThrottleAsync(read, cancellationToken).ConfigureAwait(false);
            }

            progress.Add(read);
        }
    }

    private static async Task<string> ConcatenateAsync(
        CachedTrack track,
        string targetPath,
        string? processDirectory,
        CancellationToken cancellationToken)
    {
        var working = WorkingPathFor(targetPath, processDirectory);

        await using (var output = new FileStream(
                         working,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         BufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await AppendTrackAsync(track, output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        MoveIntoPlace(working, targetPath);
        return targetPath;
    }

    private static async Task<string> MergeWithFfmpegAsync(
        IReadOnlyList<CachedTrack> tracks,
        string targetPath,
        string containerExtension,
        string ffmpegPath,
        string? processDirectory,
        CancellationToken cancellationToken)
    {
        var video = tracks.FirstOrDefault(track =>
            track.Plan.Kind.Equals("video", StringComparison.OrdinalIgnoreCase));
        var audio = tracks.FirstOrDefault(track =>
            track.Plan.Kind.Equals("audio", StringComparison.OrdinalIgnoreCase));

        if (video is null || audio is null)
        {
            video ??= tracks[0];
            audio ??= tracks[0];
        }

        var videoPayload = Path.Combine(video.Directory, "track-payload" + video.Plan.ContainerExtension);
        var audioPayload = Path.Combine(audio.Directory, "track-payload" + audio.Plan.ContainerExtension);

        await WriteTrackFileAsync(video, videoPayload, cancellationToken).ConfigureAwait(false);
        await WriteTrackFileAsync(audio, audioPayload, cancellationToken).ConfigureAwait(false);

        // ffmpeg selects the muxer from the output extension, so the temporary
        // file must keep the real container extension.
        var working = WorkingPathFor(targetPath, processDirectory) + containerExtension;

        try
        {
            await FfmpegMuxer.MuxAsync(
                    videoPayload,
                    audioPayload,
                    working,
                    ffmpegPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDelete(working);
            throw;
        }

        MoveIntoPlace(working, targetPath);
        return targetPath;
    }

    private static async Task WriteTrackFileAsync(
        CachedTrack track,
        string path,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await AppendTrackAsync(track, output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task AppendTrackAsync(
        CachedTrack track,
        Stream output,
        CancellationToken cancellationToken)
    {
        MediaInitialization? previousInitialization = null;
        var first = true;
        var buffer = new byte[1024 * 1024];

        foreach (var segment in track.Segments)
        {
            if (segment.Segment.Initialization is { } initialization &&
                (first || !Equals(initialization, previousInitialization)))
            {
                var initPath = track.Initializations
                    .First(item => Equals(item.Value, initialization))
                    .Path;

                await AppendFileAsync(initPath, output, buffer, cancellationToken)
                    .ConfigureAwait(false);
            }

            await AppendFileAsync(segment.Path, output, buffer, cancellationToken)
                .ConfigureAwait(false);

            previousInitialization = segment.Segment.Initialization;
            first = false;
        }
    }

    private static async Task AppendFileAsync(
        string path,
        Stream output,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            // Include FileShare.Delete so a pending delete can complete once the last handle closes on Windows.
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string WorkingPathFor(string targetPath, string? processDirectory) =>
        processDirectory is null
            ? targetPath + StatePaths.WorkingSuffix
            : Path.Combine(processDirectory, Path.GetFileName(targetPath) + StatePaths.WorkingSuffix);

    private static void MoveIntoPlace(string working, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(working, target, overwrite: false);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FileAccessException($"Could not move '{working}' to '{target}'.", ex);
            }
        }
    }

    private static async Task<string> FetchTextAsync(
        HttpClient client,
        Uri uri,
        RequestOptions requestOptions,
        RetryPolicy retry,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = DownloadRequestFactory.CreatePlainGet(uri, requestOptions);
                var (response, _) = await HttpSender.SendAsync(client, request, cancellationToken)
                    .ConfigureAwait(false);

                using (response)
                {
                    if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                    {
                        throw CreateHttpException(response, uri);
                    }

                    EnsureIdentityEncoding(response);

                    if (response.Content.Headers.ContentLength is > MaxManifestBytes)
                    {
                        throw new MediaManifestException($"The manifest at '{uri}' is too large.");
                    }

                    var text = await response.Content.ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (text.Length == 0)
                    {
                        throw new MediaManifestException($"The manifest at '{uri}' is empty.");
                    }

                    return text;
                }
            }
            catch (Exception ex) when (attempt < retry.MaxRetries && IsRetryable(ex, cancellationToken))
            {
                await Task.Delay(retry.GetDelay(attempt, null), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryable(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            HttpDownloadException http => http.IsTransient,
            HttpRequestException => true,
            IOException io => io.InnerException is System.Net.Sockets.SocketException,
            _ => false,
        };
    }

    private static HttpDownloadException CreateHttpException(HttpResponseMessage response, Uri uri)
    {
        var status = (int)response.StatusCode;
        var retryAfter = RetryAfterParser.Parse(response);
        var exception = new HttpDownloadException(
            $"Media request for '{uri}' failed with HTTP {status} ({response.StatusCode}).",
            RetryPolicy.IsRetryableStatus(response.StatusCode));

        if (retryAfter is { } delay)
        {
            exception.Data["RetryAfter"] = delay;
        }

        return exception;
    }

    private static void EnsureIdentityEncoding(HttpResponseMessage response)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count > 0 &&
            encodings.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnsupportedContentEncodingException(string.Join(", ", encodings));
        }
    }

    private static void PrepareCache(string cacheRoot, string fingerprint, bool restart)
    {
        if (restart && Directory.Exists(cacheRoot))
        {
            Directory.Delete(cacheRoot, recursive: true);
        }

        Directory.CreateDirectory(cacheRoot);
        var fingerprintPath = Path.Combine(cacheRoot, "plan.sha256");
        var current = File.Exists(fingerprintPath)
            ? File.ReadAllText(fingerprintPath).Trim()
            : null;

        if (current is not null && !string.Equals(current, fingerprint, StringComparison.Ordinal))
        {
            Directory.Delete(cacheRoot, recursive: true);
            Directory.CreateDirectory(cacheRoot);
        }

        if (current != fingerprint)
        {
            File.WriteAllText(fingerprintPath, fingerprint);
        }
    }

    private static string Fingerprint(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void DeleteCacheBestEffort(string cacheRoot)
    {
        try
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
        catch
        {
            // A stale cache is harmless; the next run validates the fingerprint.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record HlsResolvedPlaylist(
        Uri MediaUri,
        HlsMediaPlaylist Playlist,
        string Text,
        HlsRendition? Audio);

    private sealed record CachedInitialization(MediaInitialization Value, string Path);

    private sealed class CachedTrack
    {
        public CachedTrack(
            MediaTrackPlan plan,
            string directory,
            IReadOnlyList<CachedInitialization> initializations,
            IReadOnlyList<CachedSegment> segments)
        {
            Plan = plan;
            Directory = directory;
            Initializations = initializations;
            Segments = segments;
        }

        public MediaTrackPlan Plan { get; }

        public string Directory { get; }

        public IReadOnlyList<CachedInitialization> Initializations { get; }

        public IReadOnlyList<CachedSegment> Segments { get; }
    }

    private sealed record CachedSegment(MediaSegment Segment, string Path);

    private sealed class FetchJob
    {
        private FetchJob(Uri uri, ByteRange? range, MediaKey? key, long sequence, string path)
        {
            Uri = uri;
            Range = range;
            Key = key;
            Sequence = sequence;
            Path = path;
        }

        public Uri Uri { get; }

        public ByteRange? Range { get; }

        public MediaKey? Key { get; }

        public long Sequence { get; }

        public string Path { get; }

        public static FetchJob ForSegment(CachedSegment segment) => new(
            segment.Segment.Uri,
            segment.Segment.Range,
            segment.Segment.Key,
            segment.Segment.Sequence,
            segment.Path);

        public static FetchJob ForInitialization(CachedInitialization initialization) => new(
            initialization.Value.Uri,
            initialization.Value.Range,
            null,
            0,
            initialization.Path);
    }

    private sealed class ProgressAggregator
    {
        private readonly Action<DownloadProgress>? _onProgress;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _gate = new();
        private long _downloaded;
        private long _active;
        private long _lastReport;

        public ProgressAggregator(Action<DownloadProgress>? onProgress)
        {
            _onProgress = onProgress;
        }

        public void Enter() => Interlocked.Increment(ref _active);

        public void Leave() => Interlocked.Decrement(ref _active);

        public void Add(int bytes)
        {
            Interlocked.Add(ref _downloaded, bytes);

            var now = Stopwatch.GetTimestamp();
            var minimum = Stopwatch.Frequency / 4;
            var last = Interlocked.Read(ref _lastReport);

            if (now - last < minimum)
            {
                return;
            }

            Interlocked.Exchange(ref _lastReport, now);

            lock (_gate)
            {
                var total = Interlocked.Read(ref _downloaded);
                var seconds = Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds);
                _onProgress?.Invoke(new DownloadProgress(
                    total,
                    null,
                    total / seconds,
                    null,
                    (int)Interlocked.Read(ref _active)));
            }
        }
    }
}

/// <summary>Reads a bounded window of another stream, skipping a prefix first.</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private long _skip;
    private long _remaining;
    private long _totalRead;

    public BoundedReadStream(Stream inner, long skip, long limit, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _skip = Math.Max(0, skip);
        _remaining = Math.Max(0, limit);
        _leaveOpen = leaveOpen;
    }

    public long TotalRead => Interlocked.Read(ref _totalRead);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadCore(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => ReadCore(buffer);

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        await ReadAsyncCore(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        ReadAsyncCore(buffer, cancellationToken);

    private int ReadCore(Span<byte> buffer)
    {
        while (_skip > 0)
        {
            var want = (int)Math.Min(buffer.Length, _skip);
            if (want == 0)
            {
                return 0;
            }

            var skipped = _inner.Read(buffer[..want]);
            if (skipped == 0)
            {
                return 0;
            }

            _skip -= skipped;
        }

        if (_remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > _remaining)
        {
            buffer = buffer[..(int)_remaining];
        }

        var read = _inner.Read(buffer);
        _remaining -= read;
        Interlocked.Add(ref _totalRead, read);
        return read;
    }

    private async ValueTask<int> ReadAsyncCore(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        while (_skip > 0)
        {
            var want = (int)Math.Min(buffer.Length, _skip);
            if (want == 0)
            {
                return 0;
            }

            var skipped = await _inner.ReadAsync(buffer[..want], cancellationToken)
                .ConfigureAwait(false);
            if (skipped == 0)
            {
                return 0;
            }

            _skip -= skipped;
        }

        if (_remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > _remaining)
        {
            buffer = buffer[..(int)_remaining];
        }

        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        Interlocked.Add(ref _totalRead, read);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
