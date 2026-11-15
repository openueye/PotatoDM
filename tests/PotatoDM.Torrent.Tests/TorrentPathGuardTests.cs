namespace PotatoDM.Torrent.Tests;

/// <summary>
/// Metadata is attacker-controlled. These paths must be rejected before an
/// engine is allowed to create files from them.
/// </summary>
public class TorrentPathGuardTests
{
    private static readonly string Root = Path.Combine(
        Path.GetTempPath(),
        "potatodm-path-guard");

    [Theory]
    [InlineData("..\\..\\evil.bin")]
    [InlineData("../evil.bin")]
    [InlineData("a/../../evil.bin")]
    [InlineData("C:\\Windows\\evil.bin")]
    [InlineData("C:evil.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("a/./evil.bin")]
    public void A_file_path_outside_the_download_root_is_rejected(string path)
    {
        Assert.Throws<TorrentException>(
            () => TorrentDownloader.ValidateTorrentPaths(Root, "safe-name", [path]));
    }

    [Theory]
    [InlineData("..\\..\\evil")]
    [InlineData("../evil")]
    [InlineData("C:\\evil")]
    public void A_torrent_name_outside_the_download_root_is_rejected(string name)
    {
        Assert.Throws<TorrentException>(
            () => TorrentDownloader.ValidateTorrentPaths(Root, name, ["payload.bin"]));
    }

    [Fact]
    public void Ordinary_relative_paths_are_accepted()
    {
        TorrentDownloader.ValidateTorrentPaths(
            Root,
            "some.torrent",
            ["payload.bin", "folder/part 1.bin", "folder/sub/part2.bin"]);
    }
}
