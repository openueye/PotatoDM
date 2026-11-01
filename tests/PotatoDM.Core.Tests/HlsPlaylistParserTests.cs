using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Media;

namespace PotatoDM.Core.Tests;

public class HlsPlaylistParserTests
{
    private static readonly Uri BaseUri = new("https://example.com/live/master.m3u8");

    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv.m3u8", true)]
    [InlineData("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"x\"", true)]
    [InlineData("#EXTM3U\n#EXTINF:6,\ns.ts", false)]
    public void IsMaster_Detects_Master_Playlists(string text, bool expected)
    {
        Assert.Equal(expected, HlsPlaylistParser.IsMaster(text));
    }

    [Fact]
    public void ParseMaster_Parses_Variants_And_Resolves_Uris()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=1280000,RESOLUTION=1280x720,CODECS=\"avc1.4d401f,mp4a.40.2\",NAME=HD",
            "v0.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=640000,RESOLUTION=640x360,AUDIO=\"aud\"",
            "v1.m3u8");

        var master = HlsPlaylistParser.ParseMaster(text, BaseUri);

        Assert.Equal(2, master.Variants.Count);
        Assert.Equal(new Uri("https://example.com/live/v0.m3u8"), master.Variants[0].Uri);
        Assert.Equal(1280000, master.Variants[0].Bandwidth);
        Assert.Equal("HD", master.Variants[0].Name);
        Assert.Equal("1280x720", master.Variants[0].Resolution);
        Assert.Null(master.Variants[0].AudioGroup);

        Assert.Equal(new Uri("https://example.com/live/v1.m3u8"), master.Variants[1].Uri);
        Assert.Equal(640000, master.Variants[1].Bandwidth);
        Assert.Equal("aud", master.Variants[1].AudioGroup);
    }

    [Fact]
    public void ParseMaster_Keeps_Commas_Inside_Quoted_Codecs()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=1280000,CODECS=\"avc1.4d401f,mp4a.40.2\"",
            "v.m3u8");

        var master = HlsPlaylistParser.ParseMaster(text, BaseUri);

        Assert.Equal("avc1.4d401f,mp4a.40.2", Assert.Single(master.Variants).Codecs);
    }

    [Fact]
    public void ParseMaster_Parses_Renditions()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,URI=\"audio/en.m3u8\"",
            "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Subs\",LANGUAGE=\"en\",DEFAULT=NO,URI=\"subs/en.vtt\"");

        var master = HlsPlaylistParser.ParseMaster(text, BaseUri);

        Assert.Equal(2, master.Renditions.Count);
        Assert.Equal("AUDIO", master.Renditions[0].Type);
        Assert.Equal("aud", master.Renditions[0].GroupId);
        Assert.Equal("English", master.Renditions[0].Name);
        Assert.Equal("en", master.Renditions[0].Language);
        Assert.Equal(new Uri("https://example.com/live/audio/en.m3u8"), master.Renditions[0].Uri);
        Assert.True(master.Renditions[0].IsDefault);

        Assert.Equal("SUBTITLES", master.Renditions[1].Type);
        Assert.False(master.Renditions[1].IsDefault);
    }

    [Fact]
    public void ParseMaster_Ignores_IFrame_StreamInf()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000",
            "v0.m3u8",
            "#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=50000,URI=\"iframes.m3u8\"");

        var master = HlsPlaylistParser.ParseMaster(text, BaseUri);

        var variant = Assert.Single(master.Variants);
        Assert.Equal(new Uri("https://example.com/live/v0.m3u8"), variant.Uri);
    }

    [Fact]
    public void ParseMaster_Parses_SessionKey_Aes128_With_Iv()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x000102030405060708090A0B0C0D0E0F",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000",
            "v0.m3u8");

        var master = HlsPlaylistParser.ParseMaster(text, BaseUri);

        var key = Assert.Single(master.SessionKeys);
        Assert.Equal("AES-128", key.Method);
        Assert.Equal(new Uri("https://example.com/live/key.bin"), key.Uri);
        Assert.Null(key.KeyFormat);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), key.Iv);
    }

    [Fact]
    public void ParseMaster_SessionKey_SampleAes_Throws_Drm()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,URI=\"key.bin\"");

        var ex = Assert.Throws<DrmProtectedException>(() => HlsPlaylistParser.ParseMaster(text, BaseUri));
        Assert.Equal("SAMPLE-AES", ex.System);
    }

    [Fact]
    public void ParseMaster_SessionKey_FairPlay_KeyFormat_Throws_Drm()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-SESSION-KEY:METHOD=AES-128,KEYFORMAT=\"com.apple.streamingkeydelivery\",URI=\"skd://key\"");

        var ex = Assert.Throws<DrmProtectedException>(() => HlsPlaylistParser.ParseMaster(text, BaseUri));
        Assert.Equal("com.apple.streamingkeydelivery", ex.System);
    }

    [Fact]
    public void ParseMedia_Parses_Sequence_Endlist_And_PlaylistType()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-VERSION:3",
            "#EXT-X-TARGETDURATION:10",
            "#EXT-X-MEDIA-SEQUENCE:5",
            "#EXT-X-PLAYLIST-TYPE:VOD",
            "#EXTINF:9.5,title",
            "seg1.ts",
            "#EXTINF:10.0",
            "seg2.ts",
            "#EXT-X-ENDLIST");

        var playlist = HlsPlaylistParser.ParseMedia(text, BaseUri);

        Assert.Equal(2, playlist.Segments.Count);
        Assert.Equal(5, playlist.MediaSequence);
        Assert.Equal(10.0, playlist.TargetDuration);
        Assert.Equal("VOD", playlist.PlaylistType);
        Assert.True(playlist.EndList);

        Assert.Equal(5, playlist.Segments[0].Sequence);
        Assert.Equal(9.5, playlist.Segments[0].DurationSeconds);
        Assert.Equal(6, playlist.Segments[1].Sequence);
        Assert.Equal(10.0, playlist.Segments[1].DurationSeconds);
    }

    [Fact]
    public void ParseMedia_Key_Persists_Rotates_And_None_Resets()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"k1\",IV=0x000102030405060708090A0B0C0D0E0F",
            "#EXTINF:6,",
            "s1.ts",
            "#EXTINF:6,",
            "s2.ts",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"k2\"",
            "#EXTINF:6,",
            "s3.ts",
            "#EXT-X-KEY:METHOD=NONE",
            "#EXTINF:6,",
            "s4.ts");

        var playlist = HlsPlaylistParser.ParseMedia(text, BaseUri);
        var segments = playlist.Segments;

        Assert.Same(segments[0].Key, segments[1].Key);
        Assert.Equal(new Uri("https://example.com/live/k1"), segments[0].Key!.Uri);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), segments[0].Key!.Iv);

        Assert.Equal(new Uri("https://example.com/live/k2"), segments[2].Key!.Uri);
        Assert.Null(segments[2].Key!.Iv);

        Assert.Null(segments[3].Key);
    }

    [Fact]
    public void ParseMedia_Map_Persists_Across_Segments()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"726@0\"",
            "#EXTINF:6,",
            "s1.mp4",
            "#EXTINF:6,",
            "s2.mp4");

        var playlist = HlsPlaylistParser.ParseMedia(text, BaseUri);

        Assert.Same(playlist.Segments[0].Initialization, playlist.Segments[1].Initialization);
        var init = playlist.Segments[0].Initialization!;
        Assert.Equal(new Uri("https://example.com/live/init.mp4"), init.Uri);
        Assert.Equal(new ByteRange(0, 726), init.Range);
    }

    [Fact]
    public void ParseMedia_ByteRange_With_Offset()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-BYTERANGE:1000@200",
            "#EXTINF:6,",
            "s1.ts");

        var playlist = HlsPlaylistParser.ParseMedia(text, BaseUri);

        Assert.Equal(new ByteRange(200, 1000), playlist.Segments[0].Range);
    }

    [Fact]
    public void ParseMedia_ByteRange_Without_Offset_Continues_Per_Uri()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-BYTERANGE:500",
            "#EXTINF:6,",
            "s.ts",
            "#EXT-X-BYTERANGE:500",
            "#EXTINF:6,",
            "s.ts",
            "#EXT-X-BYTERANGE:100",
            "#EXTINF:6,",
            "t.ts");

        var playlist = HlsPlaylistParser.ParseMedia(text, BaseUri);

        Assert.Equal(new ByteRange(0, 500), playlist.Segments[0].Range);
        Assert.Equal(new ByteRange(500, 500), playlist.Segments[1].Range);
        Assert.Equal(new ByteRange(0, 100), playlist.Segments[2].Range);
    }

    [Fact]
    public void ParseMedia_Discontinuity_And_Gap()
    {
        var text = Lines(
            "#EXTM3U",
            "#EXT-X-DISCONTINUITY",
            "#EXTINF:6,",
            "s1.ts",
            "#EXT-X-GAP",
            "#EXTINF:6,",
            "s2.ts",
            "#EXTINF:6,",
            "s3.ts");

        var playlist = HlsPlaylistParser.ParseMedia(text, BaseUri);

        Assert.True(playlist.Segments[0].Discontinuity);
        Assert.False(playlist.Segments[0].Gap);

        Assert.False(playlist.Segments[1].Discontinuity);
        Assert.True(playlist.Segments[1].Gap);

        Assert.False(playlist.Segments[2].Discontinuity);
        Assert.False(playlist.Segments[2].Gap);
    }

    [Fact]
    public void ParseMedia_Handles_Bom_And_Crlf()
    {
        var text = "\uFEFF" + string.Join(
            "\r\n",
            new[] { "#EXTM3U", "#EXT-X-TARGETDURATION:6", "#EXTINF:6,", "seg.ts", "#EXT-X-ENDLIST" });

        var playlist = HlsPlaylistParser.ParseMedia(text, BaseUri);

        var segment = Assert.Single(playlist.Segments);
        Assert.Equal(new Uri("https://example.com/live/seg.ts"), segment.Uri);
        Assert.True(playlist.EndList);
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXTINF:6,\nhttp://")]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\",IV=0x000102030405060708090A0B0C0D0EZZ\n#EXTINF:6,\ns.ts")]
    [InlineData("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128\n#EXTINF:6,\ns.ts")]
    [InlineData("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-ENDLIST")]
    public void ParseMedia_Malformed_Input_Throws(string text)
    {
        Assert.Throws<MediaManifestException>(() => HlsPlaylistParser.ParseMedia(text, BaseUri));
    }

    private static string Lines(params string[] lines) => string.Join('\n', lines);
}
