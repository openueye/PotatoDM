using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Media;

namespace PotatoDM.Core.Tests;

public class FfmpegMuxerTests
{
    private const string MissingExplicitPath = @"C:\definitely-missing\ffmpeg.exe";

    [Fact]
    public void FindFfmpeg_missing_explicit_path_is_null()
    {
        Assert.Null(FfmpegMuxer.FindFfmpeg(MissingExplicitPath));
    }

    [Fact]
    public void FindFfmpeg_null_searches_path_and_returns_null_or_existing_path()
    {
        var result = FfmpegMuxer.FindFfmpeg(null);

        Assert.True(result is null || File.Exists(result));
    }

    [Fact]
    public void FindFfmpeg_blank_searches_path_and_returns_null_or_existing_path()
    {
        var result = FfmpegMuxer.FindFfmpeg("   ");

        Assert.True(result is null || File.Exists(result));
    }

    [Fact]
    public void FindFfmpeg_existing_explicit_file_is_returned_as_is()
    {
        using var temp = new TempDirectory();
        var path = temp.FileFor("ffmpeg.exe");
        File.WriteAllBytes(path, Array.Empty<byte>());

        Assert.Equal(path, FfmpegMuxer.FindFfmpeg(path));
    }

    [Fact]
    public void FindFfmpeg_existing_directory_is_null()
    {
        using var temp = new TempDirectory();
        var directory = temp.FileFor("not-an-executable");
        Directory.CreateDirectory(directory);

        Assert.Null(FfmpegMuxer.FindFfmpeg(directory));
    }

    [Fact]
    public async Task MuxAsync_missing_ffmpeg_throws_and_creates_no_output()
    {
        using var temp = new TempDirectory();
        var video = temp.FileFor("video.mp4");
        var audio = temp.FileFor("audio.m4a");
        var output = temp.FileFor("merged.mp4");

        var error = await Assert.ThrowsAsync<MediaMergeException>(() =>
            FfmpegMuxer.MuxAsync(video, audio, output, MissingExplicitPath, CancellationToken.None));

        Assert.Contains("ffmpeg", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("install", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }
}
