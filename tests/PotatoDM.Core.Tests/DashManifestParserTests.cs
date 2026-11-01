using System.Security.Cryptography;
using System.Text;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Media;

namespace PotatoDM.Core.Tests;

public class DashManifestParserTests
{
    private static readonly Uri ManifestUri = new("https://example.com/path/manifest.mpd");

    [Fact]
    public void SegmentTemplate_duration_expands_segments_and_pads_numbers()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4S" minBufferTime="PT1.5S">
              <Period duration="PT4S">
                <AdaptationSet contentType="video" mimeType="video/mp4" segmentAlignment="true">
                  <SegmentTemplate timescale="1000" duration="2000" media="seg_$Number%05d$.m4s" initialization="init.mp4" />
                  <Representation id="v0" bandwidth="800000" width="1280" height="720" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var plan = DashManifestParser.Parse(manifest, ManifestUri);

        var track = Assert.Single(plan.Tracks);
        Assert.Equal("video", track.Kind);
        Assert.Equal(".mp4", track.ContainerExtension);
        Assert.Equal(2, track.Segments.Count);

        Assert.Equal("https://example.com/path/seg_00001.m4s", track.Segments[0].Uri.AbsoluteUri);
        Assert.Equal("https://example.com/path/seg_00002.m4s", track.Segments[1].Uri.AbsoluteUri);
        Assert.Equal(1, track.Segments[0].Sequence);
        Assert.Equal(2, track.Segments[1].Sequence);
        Assert.Equal(2.0, track.Segments[0].DurationSeconds);

        Assert.NotNull(track.Segments[0].Initialization);
        Assert.Equal("https://example.com/path/init.mp4", track.Segments[0].Initialization!.Uri.AbsoluteUri);
        Assert.Null(track.Segments[0].Initialization!.Range);
    }

    [Fact]
    public void SegmentTimeline_expands_repeats_and_substitutes_time()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT6S">
              <Period duration="PT6S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentTemplate timescale="1000" media="t_$Time$.m4s" initialization="init.mp4" startNumber="5">
                    <SegmentTimeline>
                      <S t="1000" d="2000" r="1" />
                      <S d="1000" />
                    </SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="v0" bandwidth="1000000" width="640" height="360" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var plan = DashManifestParser.Parse(manifest, ManifestUri);

        var track = Assert.Single(plan.Tracks);
        Assert.Equal(3, track.Segments.Count);

        Assert.Equal("https://example.com/path/t_1000.m4s", track.Segments[0].Uri.AbsoluteUri);
        Assert.Equal("https://example.com/path/t_3000.m4s", track.Segments[1].Uri.AbsoluteUri);
        Assert.Equal("https://example.com/path/t_5000.m4s", track.Segments[2].Uri.AbsoluteUri);

        Assert.Equal(2.0, track.Segments[0].DurationSeconds);
        Assert.Equal(2.0, track.Segments[1].DurationSeconds);
        Assert.Equal(1.0, track.Segments[2].DurationSeconds);

        Assert.Equal(1, track.Segments[0].Sequence);
        Assert.Equal(2, track.Segments[1].Sequence);
        Assert.Equal(3, track.Segments[2].Sequence);
    }

    [Fact]
    public void SegmentTimeline_reject_negative_one_repeat()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT6S">
              <Period duration="PT6S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentTemplate timescale="1000" media="t_$Time$.m4s">
                    <SegmentTimeline>
                      <S t="0" d="2000" r="-1" />
                    </SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="v0" bandwidth="1000000" width="640" height="360" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        Assert.Throws<MediaManifestException>(() => DashManifestParser.Parse(manifest, ManifestUri));
    }

    [Fact]
    public void SegmentList_parses_media_ranges_and_initialization()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentList>
                    <Initialization sourceURL="init.mp4" range="0-99" />
                    <SegmentURL media="seg1.m4s" mediaRange="0-999" />
                    <SegmentURL media="seg2.m4s" mediaRange="1000-1999" />
                  </SegmentList>
                  <Representation id="v0" bandwidth="500000" width="320" height="240" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var plan = DashManifestParser.Parse(manifest, ManifestUri);

        var track = Assert.Single(plan.Tracks);
        Assert.Equal(2, track.Segments.Count);

        Assert.Equal("https://example.com/path/seg1.m4s", track.Segments[0].Uri.AbsoluteUri);
        Assert.Equal(new ByteRange(0, 1000), track.Segments[0].Range);
        Assert.Equal(1, track.Segments[0].Sequence);

        Assert.Equal("https://example.com/path/seg2.m4s", track.Segments[1].Uri.AbsoluteUri);
        Assert.Equal(new ByteRange(1000, 1000), track.Segments[1].Range);
        Assert.Equal(2, track.Segments[1].Sequence);

        Assert.NotNull(track.Segments[0].Initialization);
        Assert.Equal("https://example.com/path/init.mp4", track.Segments[0].Initialization!.Uri.AbsoluteUri);
        Assert.Equal(new ByteRange(0, 100), track.Segments[0].Initialization!.Range);
    }

    [Fact]
    public void SegmentBase_produces_a_single_full_file_segment()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="v0" bandwidth="1000000">
                    <BaseURL>movie.mp4</BaseURL>
                    <SegmentBase indexRange="100-500" />
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var plan = DashManifestParser.Parse(manifest, ManifestUri);

        var track = Assert.Single(plan.Tracks);
        var segment = Assert.Single(track.Segments);
        Assert.Equal("https://example.com/path/movie.mp4", segment.Uri.AbsoluteUri);
        Assert.Null(segment.Range);
        Assert.Null(segment.Initialization);
        Assert.Equal(1, segment.Sequence);
    }

    [Fact]
    public void BaseURLs_accumulate_across_mpd_period_and_adaptation_set()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT2S">
              <BaseURL>base/</BaseURL>
              <Period duration="PT2S">
                <BaseURL>period/</BaseURL>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <BaseURL>as/</BaseURL>
                  <SegmentTemplate timescale="1" duration="2" media="$Number$.m4s" />
                  <Representation id="v0" bandwidth="100" width="10" height="10" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var plan = DashManifestParser.Parse(manifest, ManifestUri);

        var segment = Assert.Single(Assert.Single(plan.Tracks).Segments);
        Assert.Equal("https://example.com/path/base/period/as/1.m4s", segment.Uri.AbsoluteUri);
    }

    [Fact]
    public void Selects_highest_bandwidth_video_and_audio()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT2S">
              <Period duration="PT2S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentTemplate timescale="1" duration="2" media="$RepresentationID$_$Number$.m4s" />
                  <Representation id="v0" bandwidth="100000" width="640" height="360" />
                  <Representation id="v1" bandwidth="800000" width="1920" height="1080" />
                  <Representation id="v2" bandwidth="800000" width="1280" height="720" />
                </AdaptationSet>
                <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en">
                  <SegmentTemplate timescale="1" duration="2" media="$RepresentationID$_$Number$.m4a" />
                  <Representation id="a0" bandwidth="64000" />
                  <Representation id="a1" bandwidth="128000" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var plan = DashManifestParser.Parse(manifest, ManifestUri);

        Assert.Equal(2, plan.Tracks.Count);
        Assert.True(plan.HasSeparateAudio);
        Assert.Equal(".mp4", plan.ContainerExtension);

        var video = plan.Tracks[0];
        Assert.Equal("video", video.Kind);
        var videoSegment = Assert.Single(video.Segments);
        Assert.Equal("https://example.com/path/v1_1.m4s", videoSegment.Uri.AbsoluteUri);

        var audio = plan.Tracks[1];
        Assert.Equal("audio", audio.Kind);
        Assert.Equal("en", audio.Language);
        Assert.Equal(".m4a", audio.ContainerExtension);
        var audioSegment = Assert.Single(audio.Segments);
        Assert.Equal("https://example.com/path/a1_1.m4a", audioSegment.Uri.AbsoluteUri);
    }

    [Fact]
    public void Webm_mime_type_selects_webm_and_weba_extensions()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT2S">
              <Period duration="PT2S">
                <AdaptationSet contentType="video" mimeType="video/webm">
                  <SegmentTemplate timescale="1" duration="2" media="$Number$.webm" />
                  <Representation id="v0" bandwidth="100000" width="640" height="360" />
                </AdaptationSet>
                <AdaptationSet contentType="audio" mimeType="audio/webm" lang="en">
                  <SegmentTemplate timescale="1" duration="2" media="$Number$.webm" />
                  <Representation id="a0" bandwidth="64000" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var plan = DashManifestParser.Parse(manifest, ManifestUri);

        Assert.Equal(2, plan.Tracks.Count);
        Assert.Equal(".webm", plan.Tracks[0].ContainerExtension);
        Assert.Equal(".weba", plan.Tracks[1].ContainerExtension);
        Assert.Equal(".webm", plan.ContainerExtension);
    }

    [Fact]
    public void Dynamic_manifest_is_refused()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic" mediaPresentationDuration="PT2S">
              <Period duration="PT2S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="v0" bandwidth="100000" width="640" height="360" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        Assert.Throws<MediaManifestException>(() => DashManifestParser.Parse(manifest, ManifestUri));
    }

    [Fact]
    public void Multi_period_manifest_is_refused()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4S">
              <Period duration="PT2S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="v0" bandwidth="100000" width="640" height="360" />
                </AdaptationSet>
              </Period>
              <Period duration="PT2S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="v1" bandwidth="200000" width="640" height="360" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var exception = Assert.Throws<MediaManifestException>(() => DashManifestParser.Parse(manifest, ManifestUri));
        Assert.Contains("multi-period", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Widevine_drm_is_refused()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT2S">
              <Period duration="PT2S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed" />
                  <Representation id="v0" bandwidth="100000" width="640" height="360" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var exception = Assert.Throws<DrmProtectedException>(() => DashManifestParser.Parse(manifest, ManifestUri));
        Assert.Contains("edef8ba9", exception.System, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cenc_mp4protection_drm_is_refused()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT2S">
              <Period duration="PT2S">
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011" value="cenc" />
                  <Representation id="v0" bandwidth="100000" width="640" height="360" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var exception = Assert.Throws<DrmProtectedException>(() => DashManifestParser.Parse(manifest, ManifestUri));
        Assert.Equal("CENC", exception.System);
    }

    [Fact]
    public void Missing_total_duration_is_refused()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentTemplate timescale="1" duration="2" media="$Number$.m4s" />
                  <Representation id="v0" bandwidth="100000" width="640" height="360" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        Assert.Throws<MediaManifestException>(() => DashManifestParser.Parse(manifest, ManifestUri));
    }

    [Fact]
    public void Text_subtitle_adaptation_set_is_skipped_with_a_warning()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT2S">
              <Period duration="PT2S">
                <AdaptationSet contentType="text" mimeType="text/vtt">
                  <Representation id="s0" bandwidth="100" />
                </AdaptationSet>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentTemplate timescale="1" duration="2" media="$Number$.m4s" />
                  <Representation id="v0" bandwidth="100000" width="640" height="360" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var plan = DashManifestParser.Parse(manifest, ManifestUri);

        var track = Assert.Single(plan.Tracks);
        Assert.Equal("video", track.Kind);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void Malformed_xml_is_refused()
    {
        Assert.Throws<MediaManifestException>(() => DashManifestParser.Parse("<MPD>", ManifestUri));
    }

    [Fact]
    public void Manifest_without_video_or_audio_is_refused()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT2S">
              <Period duration="PT2S">
                <AdaptationSet contentType="text" mimeType="text/vtt">
                  <Representation id="s0" bandwidth="100" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        Assert.Throws<MediaManifestException>(() => DashManifestParser.Parse(manifest, ManifestUri));
    }

    [Fact]
    public void Fingerprint_is_lowercase_sha256_of_the_manifest_text()
    {
        const string manifest =
            "<?xml version=\"1.0\"?><MPD type=\"static\" mediaPresentationDuration=\"PT2S\"><Period duration=\"PT2S\">" +
            "<AdaptationSet contentType=\"video\" mimeType=\"video/mp4\">" +
            "<SegmentTemplate timescale=\"1\" duration=\"2\" media=\"$Number$.m4s\"/>" +
            "<Representation id=\"v0\" bandwidth=\"100000\" width=\"640\" height=\"360\"/>" +
            "</AdaptationSet></Period></MPD>";

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();

        var plan = DashManifestParser.Parse(manifest, ManifestUri);

        Assert.Equal(64, plan.Fingerprint.Length);
        Assert.Equal(expected, plan.Fingerprint);
    }
}
