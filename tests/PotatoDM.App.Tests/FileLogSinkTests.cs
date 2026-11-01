using PotatoDM.AppCore.Services;
using PotatoDM.Core.Diagnostics;

namespace PotatoDM.App.Tests;

/// <summary>
/// The file sink is the only thing that turns a suppressed exception into
/// something a user can send us, so its formatting, redaction, level filter and
/// size cap are all part of the contract.
/// </summary>
public class FileLogSinkTests
{
    [Fact]
    public void Entry_carries_a_timestamp_a_level_and_the_message()
    {
        using var temp = new TempDirectory();
        var sink = new FileLogSink(temp.Path);

        sink.Log(LogLevel.Warning, "Directory 'x' could not be deleted.");

        var text = ReadToday(temp.Path);
        Assert.Contains("[WARNING]", text);
        Assert.Contains("Directory 'x' could not be deleted.", text);

        // Leading timestamp: yyyy-MM-dd HH:mm:ss.fff
        var firstLine = text.Split(Environment.NewLine)[0];
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", firstLine);
    }

    [Fact]
    public void Exception_details_are_written_under_the_message()
    {
        using var temp = new TempDirectory();
        var sink = new FileLogSink(temp.Path);

        sink.Log(
            LogLevel.Error,
            "Download job 'abc' failed.",
            new InvalidOperationException("the payload"));

        var text = ReadToday(temp.Path);
        Assert.Contains("System.InvalidOperationException: the payload", text);
    }

    [Fact]
    public void Credentials_never_reach_the_file()
    {
        using var temp = new TempDirectory();
        var sink = new FileLogSink(temp.Path);

        sink.Log(
            LogLevel.Info,
            "GET https://cdn.example.com/a.mp4?token=deadbeef with Cookie: SID=abc123");

        var text = ReadToday(temp.Path);
        Assert.DoesNotContain("deadbeef", text);
        Assert.DoesNotContain("abc123", text);
        Assert.Contains("https://cdn.example.com/a.mp4", text);
    }

    [Fact]
    public void Entries_below_the_minimum_level_are_dropped()
    {
        using var temp = new TempDirectory();
        var sink = new FileLogSink(temp.Path, minLevel: LogLevel.Warning);

        sink.Debug("debug noise");
        sink.Info("info noise");
        sink.Warning("kept");

        var text = ReadToday(temp.Path);
        Assert.DoesNotContain("debug noise", text);
        Assert.DoesNotContain("info noise", text);
        Assert.Contains("kept", text);
        Assert.False(sink.IsEnabled(LogLevel.Debug));
        Assert.True(sink.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void File_rotates_instead_of_growing_past_the_cap()
    {
        using var temp = new TempDirectory();
        const long cap = 4096;
        var sink = new FileLogSink(temp.Path, maxBytesPerFile: cap);

        var payload = new string('x', 200);
        for (var index = 0; index < 120; index++)
        {
            sink.Log(LogLevel.Info, $"entry {index} {payload}");
        }

        var current = sink.PathFor(DateTimeOffset.Now);
        var archive = Path.Combine(
            temp.Path,
            $"potatodm-{DateTimeOffset.Now:yyyyMMdd}.1.log");

        Assert.True(File.Exists(archive), "The rotated file was never created.");
        Assert.True(
            new FileInfo(current).Length < cap + 1024,
            $"Current file grew to {new FileInfo(current).Length} bytes, above the {cap} cap.");

        // The newest entry must survive the rotation: rolling must drop the oldest
        // half, not the tail the user is about to report.
        Assert.Contains("entry 119", File.ReadAllText(current));
    }

    [Fact]
    public void Unwritable_directory_does_not_throw()
    {
        using var temp = new TempDirectory();
        var blocker = temp.FileFor("blocker");
        File.WriteAllText(blocker, "not a directory");

        // 'blocker' is a file, so creating 'blocker\logs' fails.
        var sink = new FileLogSink(Path.Combine(blocker, "logs"));

        var exception = Record.Exception(() => sink.Log(LogLevel.Error, "swallowed"));

        Assert.Null(exception);
    }

    [Fact]
    public void Daily_file_name_uses_the_local_date()
    {
        using var temp = new TempDirectory();
        var sink = new FileLogSink(temp.Path);

        var expected = $"potatodm-{DateTimeOffset.Now:yyyyMMdd}.log";

        Assert.Equal(Path.Combine(temp.Path, expected), sink.PathFor(DateTimeOffset.Now));
        Assert.EndsWith(Path.Combine("PotatoDM", "logs"), AppPaths.LogsDirectory);
    }

    private static string ReadToday(string directory)
    {
        var path = Path.Combine(directory, $"potatodm-{DateTimeOffset.Now:yyyyMMdd}.log");
        Assert.True(File.Exists(path), $"Expected a log file at '{path}'.");

        return File.ReadAllText(path);
    }
}
