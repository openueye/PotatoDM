using MonoTorrent;
using MonoTorrent.BEncoding;
using PotatoDM.Torrent;

namespace PotatoDM.Torrent.Tests;

/// <summary>
/// Metadata validation and tracker injection. These tests are offline: the
/// bytes come from a locally created torrent.
/// </summary>
public class TorrentMetadataFileTests
{
    [Fact]
    public async Task Adds_one_tier_per_fallback_tracker_to_a_trackerless_torrent()
    {
        var bytes = await CreateTorrentAsync(isPrivate: false);
        var original = MonoTorrent.Torrent.Load(bytes);

        var enriched = TorrentMetadataFile.WithFallbackTrackers(bytes, enabled: true);

        Assert.NotEqual(bytes, enriched);

        var dictionary = BEncodedValue.Decode<BEncodedDictionary>(enriched);
        var tiers = Assert.IsType<BEncodedList>(dictionary["announce-list"]);

        // One URL per tier, otherwise BEP 12 tries them in order instead of
        // announcing to all of them.
        Assert.Equal(TorrentMetadataFile.FallbackTrackers.Length, tiers.Count);

        foreach (var tier in tiers.Cast<BEncodedList>())
        {
            var url = Assert.IsType<BEncodedString>(Assert.Single(tier));
            Assert.Contains(url.Text, TorrentMetadataFile.FallbackTrackers);
        }

        // Editing the top-level dictionary must not change the identity.
        var enrichedTorrent = MonoTorrent.Torrent.Load(enriched);
        Assert.Equal(original.InfoHashes.V1OrV2, enrichedTorrent.InfoHashes.V1OrV2);
    }

    [Fact]
    public async Task Leaves_private_torrents_untouched()
    {
        var bytes = await CreateTorrentAsync(isPrivate: true);

        var enriched = TorrentMetadataFile.WithFallbackTrackers(bytes, enabled: true);

        Assert.Same(bytes, enriched);
    }

    [Fact]
    public async Task Keeps_a_torrents_own_trackers_and_appends_the_fallback_tiers()
    {
        var dictionary = BEncodedValue.Decode<BEncodedDictionary>(
            await CreateTorrentAsync(isPrivate: false));
        dictionary["announce"] = new BEncodedString("http://tracker.example/announce");
        var bytes = dictionary.Encode();

        var enriched = TorrentMetadataFile.WithFallbackTrackers(bytes, enabled: true);

        Assert.NotEqual(bytes, enriched);

        var result = BEncodedValue.Decode<BEncodedDictionary>(enriched);
        var tiers = Assert.IsType<BEncodedList>(result["announce-list"]);
        var urls = tiers
            .Cast<BEncodedList>()
            .Select(tier => Assert.IsType<BEncodedString>(Assert.Single(tier)).Text)
            .ToArray();

        Assert.Equal("http://tracker.example/announce", urls[0]);
        Assert.Equal(TorrentMetadataFile.FallbackTrackers, urls.Skip(1));
    }

    [Fact]
    public async Task Rejects_metadata_for_a_different_info_hash()
    {
        var wanted = MonoTorrent.Torrent.Load(await CreateTorrentAsync(isPrivate: false));
        var other = await CreateTorrentAsync(isPrivate: false);

        Assert.False(TorrentMetadataFile.TryValidate(other, wanted.InfoHashes, out _));
    }

    [Fact]
    public async Task Accepts_and_round_trips_metadata_for_the_expected_info_hash()
    {
        var bytes = await CreateTorrentAsync(isPrivate: false);
        var torrent = MonoTorrent.Torrent.Load(bytes);

        Assert.True(TorrentMetadataFile.TryValidate(bytes, torrent.InfoHashes, out var valid));
        Assert.Same(bytes, valid);
    }

    private static async Task<byte[]> CreateTorrentAsync(bool isPrivate)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "potatodm-metadata-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            // The file name becomes the torrent name, so include a unique
            // suffix; otherwise two calls produce the same info hash.
            var payload = Path.Combine(
                directory,
                "payload-" + Guid.NewGuid().ToString("N") + ".bin");
            await File.WriteAllBytesAsync(payload, new byte[4096]);

            var torrent = await new TorrentCreator()
                .CreateAsync(new TorrentFileSource(payload));
            var bytes = torrent.Encode();

            if (!isPrivate)
            {
                return bytes;
            }

            var dictionary = BEncodedValue.Decode<BEncodedDictionary>(bytes);
            var info = Assert.IsType<BEncodedDictionary>(dictionary["info"]);
            info["private"] = new BEncodedNumber(1);
            return dictionary.Encode();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
