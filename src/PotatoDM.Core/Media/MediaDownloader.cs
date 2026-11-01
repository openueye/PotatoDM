using System.Diagnostics;
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
public static partial class MediaDownloader
{
    /// <summary>Cache directory suffix used beside the target file.</summary>
    public const string CacheSuffix = ".potatodm-media";

    private const int BufferSize = 81920;

    public static async Task<DownloadResult> DownloadAsync(
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
            ? await MediaManifestLoader.BuildHlsPlanAsync(client, manifestUri, requestOptions, retry, cancellationToken)
                .ConfigureAwait(false)
            : await MediaManifestLoader.BuildDashPlanAsync(client, manifestUri, requestOptions, retry, cancellationToken)
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
            request.MaxConnectionsPerJob,
            request.MaxConnectionsPerHost);

        var progress = new ProgressAggregator(onProgress);
        var keyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var cached = new List<CachedTrack>();

        // Keep the segment cache on failure so a retry can reuse completed parts.
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

    /// <summary>
    /// Opens one of the media working files for writing.
    ///
    /// <para>Single source of truth for the sharing mode. These files live inside
    /// the per-task process folder, which the app removes with
    /// <c>Directory.Delete(recursive: true)</c>; on Windows that marks each file
    /// delete-pending and fails while any handle lacks <c>FileShare.Delete</c>, so
    /// a merge that is still streaming must not be able to wedge a delete.</para>
    ///
    /// <para>Kept <c>internal</c> so tests can assert the sharing contract
    /// directly on the same constructor the download path uses.</para>
    /// </summary>
    internal static FileStream OpenWorkingOutput(string path) =>
        new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
