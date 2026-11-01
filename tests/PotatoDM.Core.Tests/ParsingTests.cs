using PotatoDM.Core.Http;

namespace PotatoDM.Core.Tests;

public class ContentRangeParserTests
{
    [Theory]
    [InlineData("bytes 0-99/1000", 0L, 99L, 1000L)]
    [InlineData("bytes 10-10/11", 10L, 10L, 11L)]
    [InlineData("BYTES 5-9/10", 5L, 9L, 10L)]
    [InlineData("  bytes 0-0/1  ", 0L, 0L, 1L)]
    public void Parses_closed_ranges(string header, long start, long end, long total)
    {
        Assert.True(ContentRangeParser.TryParse(header, out var range));
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.EndInclusive);
        Assert.Equal(total, range.TotalLength);
    }

    [Theory]
    [InlineData("bytes */1000", 1000L)]
    [InlineData("bytes */0", 0L)]
    public void Parses_unsatisfied_ranges(string header, long total)
    {
        Assert.True(ContentRangeParser.TryParse(header, out var range));
        Assert.Null(range.Start);
        Assert.Null(range.EndInclusive);
        Assert.Equal(total, range.TotalLength);
    }

    [Fact]
    public void Parses_unknown_total()
    {
        Assert.True(ContentRangeParser.TryParse("bytes 0-0/*", out var range));
        Assert.Equal(0, range.Start);
        Assert.Null(range.TotalLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("items 0-1/10")]
    [InlineData("bytes 0-1")]
    [InlineData("bytes 5-2/10")]
    [InlineData("bytes x-y/z")]
    [InlineData("bytes /10")]
    [InlineData("bytes 0-1/-1")]
    public void Rejects_malformed_headers(string? header)
    {
        Assert.False(ContentRangeParser.TryParse(header, out _));
    }
}

public class ContentDispositionParserTests
{
    [Theory]
    [InlineData("attachment; filename=\"hello.bin\"", "hello.bin")]
    [InlineData("attachment;filename=plain.bin", "plain.bin")]
    [InlineData("inline; filename=\"with space.txt\"", "with space.txt")]
    [InlineData("attachment; filename=\"a;b.bin\"", "a;b.bin")]
    public void Parses_plain_file_names(string header, string expected)
    {
        Assert.Equal(expected, ContentDispositionParser.GetFileName(header));
    }

    [Fact]
    public void Prefers_rfc5987_extended_name()
    {
        var header =
            "attachment; filename=\"fallback.bin\"; " +
            "filename*=UTF-8''%E4%B8%AD%E6%96%87.bin";

        Assert.Equal("中文.bin", ContentDispositionParser.GetFileName(header));
    }

    [Fact]
    public void Extended_name_wins_even_when_listed_first()
    {
        var header =
            "attachment; filename*=UTF-8''%E4%B8%AD%E6%96%87.bin; " +
            "filename=\"fallback.bin\"";

        Assert.Equal("中文.bin", ContentDispositionParser.GetFileName(header));
    }

    [Fact]
    public void Decodes_iso_8859_1_extended_name()
    {
        Assert.Equal(
            "café.bin",
            ContentDispositionParser.GetFileName("attachment; filename*=iso-8859-1''caf%E9.bin"));
    }

    [Fact]
    public void Unescapes_quoted_pairs()
    {
        Assert.Equal(
            "a\"b.bin",
            ContentDispositionParser.GetFileName("attachment; filename=\"a\\\"b.bin\""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("attachment")]
    [InlineData("attachment; size=10")]
    public void Returns_null_when_absent(string? header)
    {
        Assert.Null(ContentDispositionParser.GetFileName(header));
    }
}

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("a/b/../c.bin", "c.bin")]
    [InlineData("C:\\dir\\evil.exe", "evil.exe")]
    [InlineData("a<b>c:d|e?.bin", "abcde.bin")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("NUL", "_NUL")]
    [InlineData("name. ", "name")]
    [InlineData("中文.bin", "中文.bin")]
    public void Produces_safe_single_component_names(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("/")]
    public void Falls_back_for_unusable_names(string? input)
    {
        Assert.Equal("download.bin", FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Uses_custom_fallback()
    {
        Assert.Equal("file.bin", FileNameSanitizer.Sanitize(null, "file.bin"));
    }

    [Fact]
    public void Truncates_long_names_but_keeps_the_extension()
    {
        var result = FileNameSanitizer.Sanitize(new string('a', 300) + ".bin");

        Assert.True(result.Length <= 200);
        Assert.EndsWith(".bin", result);
        Assert.True(result.Length > 4);
    }

    [Fact]
    public void Drops_control_characters()
    {
        Assert.Equal("ab.bin", FileNameSanitizer.Sanitize("a\u0000\u0001b.bin"));
    }
}
