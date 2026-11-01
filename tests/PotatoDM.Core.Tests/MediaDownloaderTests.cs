using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Media;
using PotatoDM.Core.Models;

namespace PotatoDM.Core.Tests;

public class MediaDownloaderTests
{
    private static readonly Uri ManifestUrl = new("http://media.test/video.m3u8");

    [Fact]
    public void Detector_recognizes_manifest_urls_and_content_types()
    {
        Assert.Equal(ManifestKind.Hls, ManifestDetector.FromUrl(new Uri("https://x.test/a/index.m3u8?token=1")));
        Assert.Equal(ManifestKind.Dash, ManifestDetector.FromUrl(new Uri("https://x.test/a/manifest.mpd")));
        Assert.Null(ManifestDetector.FromUrl(new Uri("https://x.test/a/file.mp4")));
        Assert.Equal(ManifestKind.Hls, ManifestDetector.FromContentType("application/vnd.apple.mpegurl; charset=utf-8"));
        Assert.Equal(ManifestKind.Dash, ManifestDetector.FromContentType("application/dash+xml"));
        Assert.Null(ManifestDetector.FromContentType("video/mp4"));
        Assert.True(ManifestDetector.IsManifest("https://x.test/live.m3u8"));
    }

    [Fact]
    public async Task Hls_media_playlist_is_downloaded_and_concatenated_in_order()
    {
        using var temp = new TempDirectory();
        var seg0 = new byte[] { 1, 2, 3, 4 };
        var seg1 = new byte[] { 5, 6, 7, 8, 9 };
        var handler = new MediaRouteHandler(new Dictionary<string, byte[]>
        {
            ["/video.m3u8"] = Utf8(
                "#EXTM3U\n" +
                "#EXT-X-VERSION:3\n" +
                "#EXT-X-TARGETDURATION:4\n" +
                "#EXT-X-MEDIA-SEQUENCE:0\n" +
                "#EXTINF:4.0,\n" +
                "seg0.ts\n" +
                "#EXTINF:4.0,\n" +
                "seg1.ts\n" +
                "#EXT-X-ENDLIST\n"),
            ["/seg0.ts"] = seg0,
            ["/seg1.ts"] = seg1,
        });

        using var client = new HttpClient(handler);
        var target = Path.Combine(temp.Path, "movie");

        var result = await MediaDownloader.DownloadAsync(
            client,
            Request(target),
            ManifestKind.Hls,
            Probe(),
            onProgress: null,
            CancellationToken.None);

        Assert.Equal(Path.Combine(temp.Path, "movie.ts"), result.TargetPath);
        Assert.Equal(seg0.Concat(seg1), await File.ReadAllBytesAsync(result.TargetPath));
        Assert.Equal(9, result.TotalBytes);
        Assert.False(Directory.Exists(result.TargetPath + ".potatodm-media"));
    }

    [Fact]
    public async Task Hls_aes128_segments_are_decrypted()
    {
        using var temp = new TempDirectory();
        var key = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var iv = Enumerable.Range(16, 16).Select(value => (byte)value).ToArray();
        var plaintext = Encoding.UTF8.GetBytes("a small encrypted media segment");
        var ciphertext = Encrypt(plaintext, key, iv);

        var handler = new MediaRouteHandler(new Dictionary<string, byte[]>
        {
            ["/enc.m3u8"] = Utf8(
                "#EXTM3U\n" +
                "#EXT-X-TARGETDURATION:4\n" +
                "#EXT-X-MEDIA-SEQUENCE:7\n" +
                $"#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x{Convert.ToHexString(iv).ToLowerInvariant()}\n" +
                "#EXTINF:4.0,\n" +
                "seg0.ts\n" +
                "#EXT-X-ENDLIST\n"),
            ["/key.bin"] = key,
            ["/seg0.ts"] = ciphertext,
        });

        using var client = new HttpClient(handler);
        var manifestUrl = new Uri("http://media.test/enc.m3u8");

        var result = await MediaDownloader.DownloadAsync(
            client,
            Request(Path.Combine(temp.Path, "encrypted"), manifestUrl),
            ManifestKind.Hls,
            Probe(manifestUrl),
            onProgress: null,
            CancellationToken.None);

        Assert.Equal(plaintext, await File.ReadAllBytesAsync(result.TargetPath));
    }

    [Fact]
    public async Task Hls_sample_aes_is_refused_before_segments_are_fetched()
    {
        using var temp = new TempDirectory();
        var handler = new MediaRouteHandler(new Dictionary<string, byte[]>
        {
            ["/drm.m3u8"] = Utf8(
                "#EXTM3U\n" +
                "#EXT-X-TARGETDURATION:4\n" +
                "#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"key.bin\"\n" +
                "#EXTINF:4.0,\n" +
                "seg0.ts\n" +
                "#EXT-X-ENDLIST\n"),
            ["/key.bin"] = new byte[16],
            ["/seg0.ts"] = new byte[] { 1 },
        });

        using var client = new HttpClient(handler);
        var manifestUrl = new Uri("http://media.test/drm.m3u8");

        var error = await Assert.ThrowsAsync<DrmProtectedException>(() =>
            MediaDownloader.DownloadAsync(
                client,
                Request(Path.Combine(temp.Path, "drm"), manifestUrl),
                ManifestKind.Hls,
                Probe(manifestUrl),
                onProgress: null,
                CancellationToken.None));

        Assert.Contains("SAMPLE-AES", error.System);
        Assert.DoesNotContain("/key.bin", handler.RequestedPaths);
        Assert.DoesNotContain("/seg0.ts", handler.RequestedPaths);
    }

    [Fact]
    public async Task Dash_video_track_includes_the_initialization_segment()
    {
        using var temp = new TempDirectory();
        var handler = new MediaRouteHandler(new Dictionary<string, byte[]>
        {
            ["/manifest.mpd"] = Utf8(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" " +
                "mediaPresentationDuration=\"PT8S\" minBufferTime=\"PT2S\">\n" +
                "  <Period>\n" +
                "    <AdaptationSet mimeType=\"video/mp4\" contentType=\"video\">\n" +
                "      <Representation id=\"v1\" bandwidth=\"1000000\">\n" +
                "        <SegmentTemplate initialization=\"init.mp4\" media=\"seg-$Number$.m4s\" " +
                "startNumber=\"1\" duration=\"4\" timescale=\"1\"/>\n" +
                "      </Representation>\n" +
                "    </AdaptationSet>\n" +
                "  </Period>\n" +
                "</MPD>\n"),
            ["/init.mp4"] = new byte[] { 1, 2 },
            ["/seg-1.m4s"] = new byte[] { 3, 4 },
            ["/seg-2.m4s"] = new byte[] { 5, 6 },
        });

        using var client = new HttpClient(handler);
        var manifestUrl = new Uri("http://media.test/manifest.mpd");

        var result = await MediaDownloader.DownloadAsync(
            client,
            Request(Path.Combine(temp.Path, "dash"), manifestUrl),
            ManifestKind.Dash,
            Probe(manifestUrl, "application/dash+xml", "manifest.mpd"),
            onProgress: null,
            CancellationToken.None);

        Assert.Equal(Path.Combine(temp.Path, "dash.mp4"), result.TargetPath);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, await File.ReadAllBytesAsync(result.TargetPath));
    }

    [Fact]
    public async Task Hls_audio_rendition_without_ffmpeg_is_refused_with_a_clear_error()
    {
        if (FfmpegMuxer.FindFfmpeg(null) is not null)
        {
            return; // The machine has ffmpeg; the merge path is exercised elsewhere.
        }

        using var temp = new TempDirectory();
        var handler = new MediaRouteHandler(new Dictionary<string, byte[]>
        {
            ["/master.m3u8"] = Utf8(
                "#EXTM3U\n" +
                "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",DEFAULT=YES,URI=\"audio.m3u8\"\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=800000,AUDIO=\"aud\"\n" +
                "video.m3u8\n"),
            ["/video.m3u8"] = Utf8(
                "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nv0.ts\n#EXT-X-ENDLIST\n"),
            ["/audio.m3u8"] = Utf8(
                "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\na0.ts\n#EXT-X-ENDLIST\n"),
            ["/v0.ts"] = new byte[] { 1 },
            ["/a0.ts"] = new byte[] { 2 },
        });

        using var client = new HttpClient(handler);
        var manifestUrl = new Uri("http://media.test/master.m3u8");

        var error = await Assert.ThrowsAsync<MediaMergeException>(() =>
            MediaDownloader.DownloadAsync(
                client,
                Request(Path.Combine(temp.Path, "muxed"), manifestUrl),
                ManifestKind.Hls,
                Probe(manifestUrl),
                onProgress: null,
                CancellationToken.None));

        Assert.Contains("ffmpeg", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/v0.ts", handler.RequestedPaths);
    }

    private static DownloadRequest Request(string target, Uri? url = null) => new()
    {
        Url = url ?? ManifestUrl,
        TargetPath = target,
    };

    private static ProbeResult Probe(
        Uri? url = null,
        string? contentType = "application/vnd.apple.mpegurl",
        string? fileName = "video.m3u8")
    {
        var manifest = url ?? ManifestUrl;
        return new ProbeResult(
            manifest,
            manifest,
            128,
            SupportsRanges: true,
            ETag: null,
            LastModified: null,
            SuggestedFileName: fileName,
            ContentType: contentType);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Encrypt(byte[] plaintext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private sealed class MediaRouteHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, byte[]> _routes;

        public MediaRouteHandler(IReadOnlyDictionary<string, byte[]> routes)
        {
            _routes = routes;
        }

        public List<string> RequestedPaths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestedPaths.Add(path);

            if (!_routes.TryGetValue(path, out var payload))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                    RequestMessage = request,
                });
            }

            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }
}
