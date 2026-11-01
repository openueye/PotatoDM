using System.ComponentModel;
using System.Diagnostics;
using PotatoDM.Core.Exceptions;

namespace PotatoDM.Core.Media;

/// <summary>
/// Invokes the external ffmpeg binary to mux separately downloaded video and audio
/// tracks into a single container. ffmpeg is used strictly as a container muxer:
/// this client does not decrypt or otherwise process DRM-protected media.
/// </summary>
public static class FfmpegMuxer
{
    private const int StderrTailLength = 2000;

    /// <summary>
    /// Locates the ffmpeg executable. A null or blank <paramref name="explicitPath"/>
    /// searches <c>PATH</c>; an explicit path that names an existing file is returned
    /// unchanged; a bare name is searched on <c>PATH</c>. Returns null when nothing
    /// is found.
    /// </summary>
    public static string? FindFfmpeg(string? explicitPath)
    {
        if (string.IsNullOrWhiteSpace(explicitPath))
        {
            return SearchPathFor("ffmpeg");
        }

        if (HasDirectorySeparator(explicitPath))
        {
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        return SearchPathFor(explicitPath);
    }

    /// <summary>
    /// Muxes <paramref name="videoPath"/> and <paramref name="audioPath"/> into
    /// <paramref name="outputPath"/> using ffmpeg, or throws
    /// <see cref="MediaMergeException"/> when ffmpeg is unavailable, cannot be started,
    /// or reports a failure.
    /// </summary>
    public static async Task MuxAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        string? ffmpegPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var ffmpeg = FindFfmpeg(ffmpegPath);
        if (ffmpeg is null)
        {
            throw new MediaMergeException(
                "ffmpeg was not found. Install ffmpeg and make it available on PATH, or pass " +
                "its full path explicitly, to merge the separate video and audio tracks. " +
                "Alternatively, download the two tracks separately. This client uses ffmpeg " +
                "only as a container muxer and does not bypass any content protection.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
        };

        foreach (var argument in BuildArguments(videoPath, audioPath, outputPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new MediaMergeException($"Failed to start ffmpeg at '{ffmpeg}'.");
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            throw new MediaMergeException(
                $"Failed to start ffmpeg at '{ffmpeg}'. Verify that the path points to a " +
                "working ffmpeg executable.", ex);
        }

        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var tail = TakeTail(stderr, StderrTailLength);
                var message = $"ffmpeg exited with code {process.ExitCode}.";
                if (tail.Length > 0)
                {
                    message += Environment.NewLine + tail;
                }

                throw new MediaMergeException(message);
            }
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            KillProcessTree(process);
            throw new MediaMergeException($"ffmpeg failed while merging '{outputPath}'.", ex);
        }
    }

    private static bool HasDirectorySeparator(string path) =>
        path.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
        path.IndexOf(Path.AltDirectorySeparatorChar) >= 0;

    private static string? SearchPathFor(string name)
    {
        var fileName = OperatingSystem.IsWindows() &&
                       !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name + ".exe"
            : name;

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var entry in pathValue.Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            var candidate = directory.Length == 0
                ? fileName
                : Path.Combine(directory, fileName);

            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                // A malformed PATH entry is skipped.
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildArguments(
        string videoPath,
        string audioPath,
        string outputPath)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-i", videoPath,
            "-i", audioPath,
            "-map", "0:v:0",
            "-map", "1:a:0",
            "-c", "copy",
        };

        if (EndsWithAny(outputPath, ".mp4", ".m4v", ".mov"))
        {
            arguments.Add("-movflags");
            arguments.Add("+faststart");
        }
        else if (outputPath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-f");
            arguments.Add("webm");
        }

        arguments.Add(outputPath);

        return arguments;
    }

    private static bool EndsWithAny(string value, params string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string TakeTail(string value, int length) =>
        value.Length <= length ? value : value[^length..];

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process may have exited between HasExited and Kill.
        }
    }
}
