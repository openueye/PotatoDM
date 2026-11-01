using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Models;
using PotatoDM.Core.Transfer;
using static PotatoDM.Core.Media.MediaHttp;

namespace PotatoDM.Core.Media;

internal static class MediaManifestLoader
{
    internal static async Task<MediaSourcePlan> BuildHlsPlanAsync(
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

        var videoTrack = HlsTrack("video", video);
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

            var audioTrack = HlsTrack("audio", audioResolved.Playlist);
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

    internal static async Task<MediaSourcePlan> BuildDashPlanAsync(
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

    private static async Task<HlsResolvedPlaylist> ResolveHlsMediaAsync(
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
        return master.Variants
            .OrderByDescending(variant => variant.Bandwidth)
            .ThenByDescending(variant => ParseResolutionArea(variant.Resolution))
            .FirstOrDefault()
            ?? throw new MediaManifestException("The HLS master playlist contains no variants.");
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

    private static MediaTrackPlan HlsTrack(string kind, HlsMediaPlaylist playlist)
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

    private static string Fingerprint(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record HlsResolvedPlaylist(
        Uri MediaUri,
        HlsMediaPlaylist Playlist,
        string Text,
        HlsRendition? Audio);
}
