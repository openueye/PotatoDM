namespace PotatoDM.Core.Diagnostics;

/// <summary>
/// The smallest logging surface the engine needs. It lives in Core rather than
/// taking a dependency on <c>Microsoft.Extensions.Logging</c> because Core is
/// deliberately dependency-free: the CLI, the browser host and the test suite
/// all embed it, and a logging framework would drag its configuration model
/// into all three.
/// </summary>
/// <remarks>
/// Implementations must never throw. A sink that fails must swallow its own
/// failure, otherwise logging a suppressed error would surface a new one.
/// </remarks>
public interface ILogSink
{
    /// <summary>
    /// Writes one entry. <paramref name="exception"/> is null for plain events
    /// and carries the original exception for a suppressed failure.
    /// </summary>
    void Log(LogLevel level, string message, Exception? exception = null);

    /// <summary>
    /// Cheap filter so hot paths can skip building a message that would be
    /// dropped anyway. Defaults to <see langword="true"/>; sinks that filter by
    /// level should override it.
    /// </summary>
    bool IsEnabled(LogLevel level) => true;
}

/// <summary>
/// The default sink: discards everything. Used whenever no sink is injected so
/// that every existing call site keeps working unchanged.
/// </summary>
public sealed class NullLogSink : ILogSink
{
    private NullLogSink()
    {
    }

    /// <summary>The shared instance; the type holds no state.</summary>
    public static NullLogSink Instance { get; } = new();

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
    }

    public bool IsEnabled(LogLevel level) => false;
}

/// <summary>
/// Null-tolerant convenience wrappers. Written as extensions on
/// <see cref="ILogSink"/> so a caller holding a possibly-null sink writes
/// <c>_log.Warning("...")</c> instead of <c>_log?.Log(...)</c>.
/// </summary>
public static class LogSinkExtensions
{
    public static void Trace(this ILogSink? sink, string message, Exception? exception = null) =>
        sink?.Log(LogLevel.Trace, message, exception);

    public static void Debug(this ILogSink? sink, string message, Exception? exception = null) =>
        sink?.Log(LogLevel.Debug, message, exception);

    public static void Info(this ILogSink? sink, string message, Exception? exception = null) =>
        sink?.Log(LogLevel.Info, message, exception);

    public static void Warning(this ILogSink? sink, string message, Exception? exception = null) =>
        sink?.Log(LogLevel.Warning, message, exception);

    public static void Error(this ILogSink? sink, string message, Exception? exception = null) =>
        sink?.Log(LogLevel.Error, message, exception);

    /// <summary>
    /// True when the sink would accept entries at <paramref name="level"/>. Named
    /// <c>CanLog</c> rather than <c>IsEnabled</c> so it never competes with the
    /// interface member of the same signature.
    /// </summary>
    public static bool CanLog(this ILogSink? sink, LogLevel level) =>
        sink is not null && sink.IsEnabled(level);
}
