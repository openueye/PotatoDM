namespace PotatoDM.Core.Diagnostics;

/// <summary>
/// Severity of a log entry. Deliberately a small, ordered enum: the value is
/// part of the sink contract, so callers can compare levels with
/// <c>level &gt;= minimum</c> without a lookup table.
/// </summary>
public enum LogLevel
{
    /// <summary>Per-attempt chatter that is only useful while reproducing a bug.</summary>
    Trace = 0,

    /// <summary>Expected degradations and suppressed failures of optional work.</summary>
    Debug = 1,

    /// <summary>Normal lifecycle events.</summary>
    Info = 2,

    /// <summary>Something was lost or degraded, but the job may still succeed.</summary>
    Warning = 3,

    /// <summary>The job failed, or data the user cares about could not be stored.</summary>
    Error = 4,
}
