using PotatoDM.Core.Diagnostics;

namespace PotatoDM.Core.Tests;

/// <summary>
/// Download logs get mailed to support, so they are effectively public. These
/// tests pin the three shapes that actually carry credentials.
/// </summary>
public class LogScrubberTests
{
    [Theory]
    [InlineData("Cookie: SID=abc123; token=zzz")]
    [InlineData("cookie:SID=abc123")]
    [InlineData("Set-Cookie: SID=abc123; Path=/")]
    public void Cookie_headers_are_redacted(string text)
    {
        var scrubbed = LogScrubber.Scrub(text);

        Assert.DoesNotContain("abc123", scrubbed);
        Assert.DoesNotContain("zzz", scrubbed);
        Assert.Contains("<redacted>", scrubbed);
    }

    [Fact]
    public void Authorization_header_is_redacted_but_its_name_survives()
    {
        var scrubbed = LogScrubber.Scrub("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig");

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", scrubbed);
        Assert.Contains("Authorization", scrubbed);
        Assert.Contains("<redacted>", scrubbed);
    }

    [Fact]
    public void Url_keeps_scheme_host_and_path_but_loses_the_query_string()
    {
        var scrubbed = LogScrubber.Scrub(
            "Probing https://cdn.example.com/media/movie.mp4?token=deadbeef&expires=12345 failed.");

        Assert.Contains("https://cdn.example.com/media/movie.mp4", scrubbed);
        Assert.DoesNotContain("deadbeef", scrubbed);
        Assert.DoesNotContain("expires=12345", scrubbed);
        Assert.Contains("failed.", scrubbed);
    }

    [Theory]
    [InlineData("token=deadbeef")]
    [InlineData("api_key=deadbeef")]
    [InlineData("signature=deadbeef")]
    [InlineData("password=deadbeef")]
    [InlineData("sessionId=deadbeef")]
    public void Secret_key_value_pairs_are_redacted(string text)
    {
        var scrubbed = LogScrubber.Scrub("request failed for " + text + " and stopped");

        Assert.DoesNotContain("deadbeef", scrubbed);
        Assert.Contains("stopped", scrubbed);
    }

    [Fact]
    public void Ordinary_messages_are_left_alone()
    {
        const string Message =
            "Directory 'D:\\Downloads\\movie' could not be deleted after 5 attempts; " +
            "it is left on disk.";

        Assert.Equal(Message, LogScrubber.Scrub(Message));
    }

    [Fact]
    public void Null_and_empty_inputs_are_safe()
    {
        Assert.Equal(string.Empty, LogScrubber.Scrub(null));
        Assert.Equal(string.Empty, LogScrubber.Scrub(string.Empty));
    }

    [Fact]
    public void Noop_sink_accepts_everything_and_reports_disabled()
    {
        var sink = NullLogSink.Instance;

        sink.Log(LogLevel.Trace, "ignored");
        sink.Log(LogLevel.Error, "ignored", new InvalidOperationException("boom"));

        Assert.False(sink.IsEnabled(LogLevel.Error));
        Assert.Same(NullLogSink.Instance, NullLogSink.Instance);
    }

    [Fact]
    public void Extension_helpers_tolerate_a_null_sink()
    {
        ILogSink? sink = null;

        // Must not throw: every injected seam defaults to null in older call sites.
        sink.Debug("no sink");
        sink.Warning("no sink", new InvalidOperationException("boom"));
        sink.Error("no sink");

        Assert.False(sink.CanLog(LogLevel.Error));
    }
}
