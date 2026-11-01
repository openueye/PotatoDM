namespace PotatoDM.Core.Models;

/// <summary>
/// The complete sidecar state for a job. Written atomically, so a crash can
/// never leave a truncated JSON document behind.
/// </summary>
public sealed class JobState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Url { get; set; } = string.Empty;

    public string FinalUrl { get; set; } = string.Empty;

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public long TotalLength { get; set; }

    public DownloadMode Mode { get; set; }

    public string TargetFile { get; set; } = string.Empty;

    public string WorkingFile { get; set; } = string.Empty;

    public List<DownloadSegment> Segments { get; set; } = new();

    public JobStatus Status { get; set; } = JobStatus.Pending;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public long BytesDownloaded => Segments.Sum(s => s.BytesDone);

    /// <summary>
    /// Copies the state with the segment counters frozen at this instant.
    /// Workers keep advancing the live objects, so a snapshot must be taken
    /// before flushing the data file and then written to the sidecar.
    /// </summary>
    public JobState Snapshot() => new()
    {
        SchemaVersion = SchemaVersion,
        Url = Url,
        FinalUrl = FinalUrl,
        ETag = ETag,
        LastModified = LastModified,
        TotalLength = TotalLength,
        Mode = Mode,
        TargetFile = TargetFile,
        WorkingFile = WorkingFile,
        Segments = Segments
            .Select(segment => new DownloadSegment
            {
                Index = segment.Index,
                Start = segment.Start,
                EndExclusive = segment.EndExclusive,
                BytesDone = segment.BytesDone,
            })
            .ToList(),
        Status = Status,
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
    };
}
