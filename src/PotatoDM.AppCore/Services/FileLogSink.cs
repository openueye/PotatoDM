using System.Security;
using System.Text;
using PotatoDM.Core.Diagnostics;

namespace PotatoDM.AppCore.Services;

/// <summary>
/// Appends log entries to <c>potatodm-yyyyMMdd.log</c> beside the app registry.
/// </summary>
/// <remarks>
/// <para>
/// One file per day, capped at <see cref="DefaultMaxBytesPerFile"/>: when a day's
/// file would grow past the cap it is rotated to <c>potatodm-yyyyMMdd.1.log</c>
/// (overwriting the previous rotation) and a fresh file is started. That keeps
/// the worst case at two files per day without a background timer.
/// </para>
/// <para>
/// This lives in AppCore rather than in the WPF executable on purpose: the test
/// projects target <c>net8.0</c> and cannot reference a <c>net8.0-windows</c>
/// WPF assembly, so a sink placed in the exe would be untestable. AppCore is
/// still outside <c>PotatoDM.Core</c>, which keeps the "Core has no third-party
/// dependencies" constraint intact — this type only uses the BCL.
/// </para>
/// </remarks>
public sealed class FileLogSink : ILogSink
{
    public const long DefaultMaxBytesPerFile = 2L * 1024 * 1024;

    public const int DefaultRetentionDays = 7;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly long _maxBytesPerFile;
    private readonly int _retentionDays;
    private bool _pruned;
    private long _currentLength = -1;
    private string? _currentPath;

    public FileLogSink(
        string directory,
        LogLevel minLevel = LogLevel.Info,
        long maxBytesPerFile = DefaultMaxBytesPerFile,
        int retentionDays = DefaultRetentionDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        _minLevel = minLevel;
        _maxBytesPerFile = Math.Max(4 * 1024, maxBytesPerFile);
        _retentionDays = Math.Max(1, retentionDays);
    }

    /// <summary>Directory that receives the daily files.</summary>
    public string DirectoryPath => _directory;

    /// <summary>Levels below this are dropped before any string is composed.</summary>
    public LogLevel MinimumLevel => _minLevel;

    public bool IsEnabled(LogLevel level) => level >= _minLevel;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minLevel)
        {
            return;
        }

        var entry = Compose(level, message, exception);
        if (entry is null)
        {
            return;
        }

        Write(entry);
    }

    /// <summary>
    /// Path this sink would use for <paramref name="date"/>. Exposed so tests and
    /// the "open log folder" action agree with the writer.
    /// </summary>
    public string PathFor(DateTimeOffset date) =>
        Path.Combine(_directory, $"potatodm-{date:yyyyMMdd}.log");

    /// <summary>
    /// Builds one entry. Returns null when scrubbing failed, in which case the
    /// entry is dropped rather than written uninspected.
    /// </summary>
    private static string? Compose(LogLevel level, string message, Exception? exception)
    {
        var safeMessage = LogScrubber.Redact(message);
        if (safeMessage is null)
        {
            return null;
        }

        var builder = new StringBuilder(safeMessage.Length + 64);
        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        builder.Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ");
        builder.Append(safeMessage);

        if (exception is not null)
        {
            var safeException = LogScrubber.Redact($"{exception.GetType().FullName}: {exception.Message}");
            builder.AppendLine();
            builder.Append("    ").Append(safeException ?? "<exception text withheld>");

            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                var safeStack = LogScrubber.Redact(exception.StackTrace);
                if (safeStack is not null)
                {
                    builder.AppendLine();
                    builder.Append(safeStack);
                }
            }
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private void Write(string entry)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                PruneOnce();

                var now = DateTimeOffset.Now;
                var path = PathFor(now);
                var length = CurrentLength(path);

                // +3 for the CRLF of a second line: the entry may carry a stack.
                if (length > 0 && length + entry.Length > _maxBytesPerFile)
                {
                    Rotate(path);
                    path = PathFor(now);
                    length = 0;
                }

                File.AppendAllText(path, entry, Utf8NoBom);
                RememberCurrentFile(path, length + entry.Length);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or PathTooLongException or SecurityException)
            {
                // A sink must never take the app down; dropping the entry is the
                // only acceptable failure mode.
            }
        }
    }

    /// <summary>
    /// Cached length of the file we last appended to. Recomputing a FileInfo for
    /// every entry is measurable during a 4-per-second progress stream.
    /// </summary>
    private long CurrentLength(string path)
    {
        if (_currentPath == path && _currentLength >= 0)
        {
            return _currentLength;
        }

        try
        {
            var info = new FileInfo(path);
            RememberCurrentFile(path, info.Exists ? info.Length : 0);
            return _currentLength;
        }
        catch (IOException)
        {
            RememberCurrentFile(path, 0);
            return 0;
        }
    }

    private void RememberCurrentFile(string path, long length)
    {
        _currentPath = path;
        _currentLength = length;
    }

    private void Rotate(string path)
    {
        var archive = Path.Combine(
            _directory,
            $"potatodm-{DateTimeOffset.Now:yyyyMMdd}.1.log");

        // Overwriting is intended: the second rotation of a day throws away the
        // oldest half of that day, which is the behaviour a hard cap implies.
        File.Move(path, archive, overwrite: true);
        RememberCurrentFile(path, 0);
    }

    /// <summary>Deletes daily files older than the retention window, once per run.</summary>
    private void PruneOnce()
    {
        if (_pruned)
        {
            return;
        }

        _pruned = true;
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);

        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "potatodm-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
