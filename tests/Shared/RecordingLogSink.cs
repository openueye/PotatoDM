using System.Collections.Concurrent;
using PotatoDM.Core.Diagnostics;

namespace PotatoDM.Tests;

/// <summary>One captured log entry.</summary>
public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// An <see cref="ILogSink"/> that keeps everything in memory so a test can assert
/// on what would have been written to disk.
/// </summary>
public sealed class RecordingLogSink : ILogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>Entries captured so far, in call order.</summary>
    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    /// <summary>Every message joined into one string, for readable failure messages.</summary>
    public string Transcript =>
        string.Join(" | ", _entries.Select(entry => $"{entry.Level}: {entry.Message}"));

    public bool IsEnabled(LogLevel level) => true;

    public void Log(LogLevel level, string message, Exception? exception = null) =>
        _entries.Enqueue(new LogEntry(level, message, exception));

    /// <summary>True when an entry at <paramref name="level"/> contains the text.</summary>
    public bool Has(LogLevel level, string contains) => Find(level, contains) is not null;

    /// <summary>True when any level contains the text (case-sensitive).</summary>
    public bool HasAny(string contains) =>
        _entries.Any(entry => entry.Message.Contains(contains, StringComparison.Ordinal));

    /// <summary>First matching entry, or null.</summary>
    public LogEntry? Find(LogLevel level, string contains) =>
        _entries.FirstOrDefault(entry =>
            entry.Level == level &&
            entry.Message.Contains(contains, StringComparison.Ordinal));
}
