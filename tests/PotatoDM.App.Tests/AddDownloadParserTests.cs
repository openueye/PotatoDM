using PotatoDM.AppCore.Services;

namespace PotatoDM.App.Tests;

public class AddDownloadParserTests
{
    private const string Magnet =
        "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=example";

    [Fact]
    public void Accepts_an_http_url()
    {
        Assert.True(AddDownloadParser.TryParse(
            "https://example.test/file.zip",
            out var sources,
            out var error));

        Assert.Null(error);
        Assert.Single(sources);
        Assert.Equal("https://example.test/file.zip", sources[0]);
    }

    [Fact]
    public void Accepts_one_source_per_line_and_ignores_blank_lines()
    {
        var text = "https://example.test/a.bin\r\n\r\n  " + Magnet + "  \n";

        Assert.True(AddDownloadParser.TryParse(text, out var sources, out var error));

        Assert.Null(error);
        Assert.Equal(2, sources.Count);
        Assert.Equal("https://example.test/a.bin", sources[0]);
        Assert.Equal(Magnet, sources[1]);
    }

    [Fact]
    public void Reports_the_line_number_for_an_invalid_entry()
    {
        Assert.False(AddDownloadParser.TryParse(
            "https://example.test/a.bin\nftp://example.test/b.bin",
            out _,
            out var error));

        Assert.Contains("第 2 行", error);
    }

    [Fact]
    public void Rejects_empty_input()
    {
        Assert.False(AddDownloadParser.TryParse("  \r\n ", out var sources, out var error));

        Assert.Empty(sources);
        Assert.NotNull(error);
    }
}
