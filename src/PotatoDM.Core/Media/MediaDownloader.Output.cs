using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Storage;

namespace PotatoDM.Core.Media;

public static partial class MediaDownloader
{
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

    private static async Task<string> ConcatenateAsync(
        CachedTrack track,
        string targetPath,
        string? processDirectory,
        CancellationToken cancellationToken)
    {
        var working = WorkingPathFor(targetPath, processDirectory);

        var output = OpenWorkingOutput(working);
        await using (output.ConfigureAwait(false))
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
        var output = OpenWorkingOutput(path);
        await using var outputScope = output.ConfigureAwait(false);

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
        var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            // Include FileShare.Delete so a pending delete can complete once the last handle closes on Windows.
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var inputScope = input.ConfigureAwait(false);

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
}
