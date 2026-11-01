namespace PotatoDM.Torrent.Tests;

public class TorrentSourceTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void IsMagnet_is_case_insensitive_and_rejects_other_schemes()
    {
        Assert.True(TorrentSource.IsMagnet("MAGNET:?xt=urn:btih:" + Hash));
        Assert.False(TorrentSource.IsMagnet("https://example.test/file.torrent"));
        Assert.False(TorrentSource.IsMagnet(null));
    }

    [Fact]
    public void SuggestedName_prefers_the_display_name_and_strips_invalid_characters()
    {
        var source = "magnet:?xt=urn:btih:" + Hash + "&dn=My%3AFile%2FName.iso";

        Assert.Equal("My_File_Name.iso", TorrentSource.SuggestedName(source));
    }

    [Fact]
    public void SuggestedName_falls_back_to_the_info_hash()
    {
        var source = "magnet:?xt=urn:btih:" + Hash;

        Assert.Equal("torrent-0123456789ab", TorrentSource.SuggestedName(source));
    }

    [Fact]
    public void Validate_accepts_a_real_magnet_link()
    {
        TorrentSource.Validate("magnet:?xt=urn:btih:" + Hash + "&dn=test");
    }

    [Fact]
    public void Validate_rejects_a_malformed_magnet_link()
    {
        Assert.Throws<TorrentException>(
            () => TorrentSource.Validate("magnet:?xt=urn:btih:not-a-hash"));
    }

    [Fact]
    public void InfoHash_returns_the_btih_value()
    {
        Assert.Equal(Hash, TorrentSource.InfoHash("magnet:?xt=urn:btih:" + Hash));
        Assert.Null(TorrentSource.InfoHash("magnet:?dn=missing-hash"));
    }
}
