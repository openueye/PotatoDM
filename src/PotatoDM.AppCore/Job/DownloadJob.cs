using System.ComponentModel;
using System.Runtime.CompilerServices;
using PotatoDM.AppCore.Models;
using PotatoDM.Core.Formatting;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;

namespace PotatoDM.AppCore.Job;

/// <summary>
/// Observable state for one download, bound directly by the WPF view.
/// Engine events are marshalled here through <c>IUiDispatcher</c>.
/// </summary>
public sealed class DownloadJob : INotifyPropertyChanged
{
    private DownloadJobState _state = DownloadJobState.Probing;
    private string _targetPath = string.Empty;
    private string _statePath = string.Empty;
    private string _taskDirectory = string.Empty;
    private string? _displayName;
    private long _totalBytes;
    private long _bytesDownloaded;
    private double _bytesPerSecond;
    private TimeSpan? _eta;
    private int _activeConnections;
    private bool _torrentChecking;
    private bool _torrentHasMetadata;
    private bool _mediaFinalizing;
    private string? _error;
    private bool _hasStateFile;
    private IReadOnlyList<DownloadSegmentProgress> _segmentProgresses = Array.Empty<DownloadSegmentProgress>();

    public DownloadJob(string id, string url, DateTimeOffset createdUtc)
    {
        Id = id;
        Url = url;
        CreatedUtc = createdUtc;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Url { get; }

    public DateTimeOffset CreatedUtc { get; }

    public string TargetPath => _targetPath;

    public string StatePath => _statePath;

    /// <summary>
    /// Per-task folder holding the finished file and its <c>.potatodm</c> process
    /// directory. Empty for records created before the per-task layout.
    /// </summary>
    public string TaskDirectory => _taskDirectory;

    /// <summary>Folder beside the target that holds sidecars, working data and caches.</summary>
    public string ProcessDirectory => string.IsNullOrEmpty(_taskDirectory)
        ? string.Empty
        : Path.Combine(_taskDirectory, StatePaths.ProcessDirectoryName);

    public DownloadJobState State => _state;

    public long TotalBytes => _totalBytes;

    public long BytesDownloaded => _bytesDownloaded;

    public double BytesPerSecond => _bytesPerSecond;

    public TimeSpan? Eta => _eta;

    public int ActiveConnections => _activeConnections;

    public string? Error => _error;

    public JobFailureKind FailureKind { get; private set; }
    public bool IsFinalizing => _mediaFinalizing && _state == DownloadJobState.Downloading;

    public string FailureSummary => FailureKind switch
    {
        JobFailureKind.Authentication => "访问被拒绝，请回原网页重新接管。",
        JobFailureKind.Storage => "无法写入，请检查目录权限和磁盘空间。",
        JobFailureKind.MediaTool => "媒体合并失败，请检查 ffmpeg 设置。",
        JobFailureKind.UnsupportedMedia => "媒体或内容格式不受支持，请查看详情。",
        _ => string.IsNullOrEmpty(TargetPath) ? "连接失败，请检查网络后重试连接。" : "下载失败，请查看详情并继续。",
    };

    public bool HasStateFile => _hasStateFile;

    /// <summary>Immutable snapshot for the visual chunk bar under the total progress.</summary>
    public IReadOnlyList<DownloadSegmentProgress> SegmentProgresses => _segmentProgresses;

    public bool HasSegmentProgress => SegmentProgresses.Count > 1;

    public string SegmentSummary =>
        !HasSegmentProgress
            ? string.Empty
            : IsTorrent || IsMedia
                ? $"{SegmentProgresses.Count} 块"
                : $"{SegmentProgresses.Count} 段并行";

    /// <summary>HLS/DASH job: progress lives in the media cache, not a sidecar.</summary>
    internal bool IsMedia { get; set; }

    /// <summary>Magnet/BitTorrent job: progress lives in the torrent's own files and metadata cache.</summary>
    internal bool IsTorrent { get; set; }

    /// <summary>
    /// Media and torrent jobs can always retry because their caches survive.
    /// A job whose failed artifacts were cleaned can start over from zero.
    /// </summary>
    public bool CanResume =>
        _state is DownloadJobState.Paused or DownloadJobState.Failed
        && FailureKind != JobFailureKind.UnsupportedMedia
        && (_hasStateFile || IsMedia || IsTorrent || StartOverOnResume);

    /// <summary>
    /// A plain HTTP job can be downloaded again from zero in the same folder.
    ///
    /// <para>Completed is excluded on purpose: the target file is already there, so
    /// restarting would resolve to "name (1)" and leave a duplicate. Torrent and
    /// media jobs are excluded because they have no restart concept — libtorrent
    /// re-hashes existing pieces and media reuses its fingerprinted cache, so for
    /// them "restart" is indistinguishable from "resume"; a button that lights up
    /// and does nothing is worse than one that stays disabled.</para>
    /// </summary>
    public bool CanRestart =>
        !IsTorrent
        && !IsMedia
        && FailureKind != JobFailureKind.UnsupportedMedia
        && _state is DownloadJobState.Failed or DownloadJobState.Paused;

    public string RestartLabel => string.IsNullOrEmpty(TargetPath) ? "重试连接" : "重新开始";

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(_displayName)
            ? _displayName
            : string.IsNullOrEmpty(_targetPath)
                ? Url
                : Path.GetFileName(_targetPath);

    public double PercentValue
    {
        get
        {
            if (_totalBytes > 0)
            {
                return Math.Clamp((double)_bytesDownloaded / _totalBytes * 100.0, 0, 100);
            }

            if (IsMedia && _segmentProgresses.Count > 0)
            {
                var sum = 0.0;

                foreach (var segment in _segmentProgresses)
                {
                    sum += segment.Percent;
                }

                return Math.Clamp(sum / _segmentProgresses.Count, 0, 100);
            }

            return 0;
        }
    }

    public string ProgressText => _mediaFinalizing && _state == DownloadJobState.Downloading
        ? "合并中"
        :
        _totalBytes > 0 || (IsMedia && _segmentProgresses.Count > 0)
            ? $"{PercentValue:0.0}%"
            : "—";

    public string SizeText =>
        _totalBytes > 0
            ? IsMedia && _state != DownloadJobState.Completed
                ? $"约 {ByteSize.Format(_totalBytes)}"
                : ByteSize.Format(_totalBytes)
            : "—";

    public string TransferredText =>
        _torrentChecking
            ? $"校验进度 {PercentValue:0.0}%"
            : IsTorrent && _state == DownloadJobState.Downloading
                ? $"{ByteSize.Format(_bytesDownloaded)} / {(_totalBytes > 0 ? ByteSize.Format(_totalBytes) : "—")} · {_activeConnections} 个节点"
            : _totalBytes > 0
                ? IsMedia && _state != DownloadJobState.Completed
                    ? $"{ByteSize.Format(_bytesDownloaded)} / 约 {ByteSize.Format(_totalBytes)}"
                    : $"{ByteSize.Format(_bytesDownloaded)} / {ByteSize.Format(_totalBytes)}"
                : IsMedia
                    ? $"已下载 {ByteSize.Format(_bytesDownloaded)}"
                    : ByteSize.Format(_bytesDownloaded);

    public string SpeedText =>
        _state == DownloadJobState.Downloading && _bytesPerSecond > 0
            ? ByteSize.FormatSpeed(_bytesPerSecond)
            : "—";

    public string EtaText =>
        _state == DownloadJobState.Downloading && _eta is { } eta
            ? FormatEta(eta)
            : "—";

    public string StateText => _state switch
    {
        DownloadJobState.Probing => "探测中",
        DownloadJobState.Queued => "排队中",
        DownloadJobState.Downloading => _mediaFinalizing ? "合并中" :
            IsMedia ? "下载分片" : IsTorrent ?
                _torrentChecking ? "校验中" : !_torrentHasMetadata ? "获取元数据" :
                _activeConnections == 0 ? "连接节点" : "下载中" : "下载中",
        DownloadJobState.Paused => "已暂停",
        DownloadJobState.Completed => "已完成",
        DownloadJobState.Failed => "失败",
        _ => "未知",
    };

    public bool CanPause =>
        _state is DownloadJobState.Queued or DownloadJobState.Downloading;

    public bool CanOpen =>
        _state == DownloadJobState.Completed && !string.IsNullOrEmpty(_targetPath);

    internal CancellationTokenSource? Cancellation { get; set; }

    /// <summary>
    /// True while a fresh job has been accepted but has not written a sidecar
    /// yet. Pausing it from the queue is therefore reversible by starting over.
    /// </summary>
    internal bool PendingFreshStart { get; set; }

    /// <summary>Connections chosen when the job entered the queue.</summary>
    internal int PendingConnections { get; set; }

    /// <summary>
    /// True when a failed job's process artifacts were deleted, so "resume"
    /// means starting a fresh transfer in the same task folder.
    /// </summary>
    internal bool StartOverOnResume { get; set; }

    /// <summary>Browser-supplied headers/cookies used for probes and resumes.</summary>
    internal JobRequestMetadata? RequestMetadata { get; set; }

    public void BindTarget(string targetPath, string statePath, string? taskDirectory = null)
    {
        _targetPath = targetPath;
        _statePath = statePath;

        if (!string.IsNullOrEmpty(taskDirectory))
        {
            _taskDirectory = taskDirectory;
        }

        RaiseAll();
    }

    public void ResolveTarget(
        string targetPath,
        string statePath,
        long totalBytes,
        string? taskDirectory = null)
    {
        _targetPath = targetPath;
        _statePath = statePath;
        _totalBytes = totalBytes;
        _hasStateFile = true;
        StartOverOnResume = false;

        if (!string.IsNullOrEmpty(taskDirectory))
        {
            _taskDirectory = taskDirectory;
        }

        RaiseAll();
    }

    public void SetHasStateFile(bool value)
    {
        _hasStateFile = value;
        RaiseAll();
    }

    /// <summary>
    /// Shows a friendlier name than the raw source URL. A magnet only learns its
    /// real torrent name after the metadata exchange, so this is refreshed from
    /// the downloader's progress.
    /// </summary>
    public void SetDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(_displayName, name, StringComparison.Ordinal))
        {
            return;
        }

        _displayName = name;
        Raise(nameof(DisplayName));
    }

    public void MarkProbing()
    {
        _state = DownloadJobState.Probing;
        _error = null;
        FailureKind = JobFailureKind.Other;
        RaiseAll();
    }

    public void MarkQueued()
    {
        _state = DownloadJobState.Queued;
        _bytesPerSecond = 0;
        _eta = null;
        _activeConnections = 0;
        _torrentChecking = false;
        _torrentHasMetadata = false;
        _mediaFinalizing = false;
        _error = null;
        FailureKind = JobFailureKind.Other;
        RaiseAll();
    }

    public void MarkDownloading()
    {
        _state = DownloadJobState.Downloading;
        _torrentChecking = false;
        _error = null;
        RaiseAll();
    }

    /// <summary>
    /// libtorrent re-hashes existing pieces before resuming; during that phase
    /// there are no peers and no download rate, so the row says "校验中".
    /// </summary>
    internal void SetTorrentChecking(bool checking)
    {
        if (_torrentChecking == checking)
        {
            return;
        }

        _torrentChecking = checking;
        Raise(nameof(StateText));
        Raise(nameof(TransferredText));
    }

    internal void SetTorrentMetadata(bool hasMetadata)
    {
        if (_torrentHasMetadata == hasMetadata) return;
        _torrentHasMetadata = hasMetadata;
        Raise(nameof(StateText));
    }

    public void ApplyProgress(DownloadProgress progress)
    {
        if (_mediaFinalizing != progress.IsFinalizing)
        {
            _mediaFinalizing = progress.IsFinalizing;
            Raise(nameof(StateText));
            Raise(nameof(ProgressText));
            Raise(nameof(IsFinalizing));
        }
        var downloaded = Math.Max(0, progress.BytesDownloaded);
        var total = progress.TotalBytes is { } reportedTotal && reportedTotal >= 0
            ? reportedTotal
            : IsMedia ? 0 : _totalBytes;
        var bytesChanged = downloaded != _bytesDownloaded;
        var totalChanged = total != _totalBytes;
        var speedChanged = progress.BytesPerSecond != _bytesPerSecond;
        var etaChanged = progress.Eta != _eta;
        var connectionsChanged = progress.ActiveConnections != _activeConnections;

        _bytesDownloaded = downloaded;
        _totalBytes = total;
        _bytesPerSecond = progress.BytesPerSecond;
        _eta = progress.Eta;
        _activeConnections = progress.ActiveConnections;

        var (segmentsChanged, segmentContentChanged) = UpdateSegmentProgresses(progress.Segments);

        // Only the values a progress tick actually changed. The WPF bindings and
        // MainViewModel subscribe to these names; raising the other twenty per
        // tick made every row re-query commands and recompute the status line.
        if (bytesChanged || totalChanged)
        {
            Raise(nameof(BytesDownloaded));
            Raise(nameof(TotalBytes));
            Raise(nameof(PercentValue));
            Raise(nameof(ProgressText));
            Raise(nameof(SizeText));
            Raise(nameof(TransferredText));
        }

        if (speedChanged)
        {
            Raise(nameof(BytesPerSecond));
            Raise(nameof(SpeedText));
        }

        if (etaChanged)
        {
            Raise(nameof(Eta));
            Raise(nameof(EtaText));
        }

        if (connectionsChanged)
        {
            Raise(nameof(ActiveConnections));
            if (IsTorrent) Raise(nameof(StateText));
            if (IsTorrent) Raise(nameof(TransferredText));
        }

        if (segmentContentChanged)
        {
            Raise(nameof(SegmentProgresses));
        }

        if (segmentsChanged)
        {
            Raise(nameof(HasSegmentProgress));
            Raise(nameof(SegmentSummary));
        }
    }

    public void MarkCompleted(long totalBytes)
    {
        if (totalBytes > 0)
        {
            _totalBytes = totalBytes;
        }

        _bytesDownloaded = _totalBytes;
        _bytesPerSecond = 0;
        _eta = TimeSpan.Zero;
        _activeConnections = 0;
        _error = null;
        _state = DownloadJobState.Completed;
        RaiseAll();
    }

    public void MarkPaused()
    {
        _bytesPerSecond = 0;
        _eta = null;
        _activeConnections = 0;
        _state = DownloadJobState.Paused;
        RaiseAll();
    }

    public void MarkFailed(string message, JobFailureKind kind = JobFailureKind.Other)
    {
        _bytesPerSecond = 0;
        _eta = null;
        _activeConnections = 0;
        _error = message;
        FailureKind = kind;
        _state = DownloadJobState.Failed;
        RaiseAll();
    }

    public void RequestCancel()
    {
        try
        {
            Cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(State));
        Raise(nameof(StateText));
        Raise(nameof(IsFinalizing));
        Raise(nameof(TargetPath));
        Raise(nameof(StatePath));
        Raise(nameof(DisplayName));
        Raise(nameof(TotalBytes));
        Raise(nameof(BytesDownloaded));
        Raise(nameof(BytesPerSecond));
        Raise(nameof(Eta));
        Raise(nameof(ActiveConnections));
        Raise(nameof(Error));
        Raise(nameof(FailureKind));
        Raise(nameof(FailureSummary));
        Raise(nameof(HasStateFile));
        Raise(nameof(HasSegmentProgress));
        Raise(nameof(SegmentSummary));
        Raise(nameof(PercentValue));
        Raise(nameof(ProgressText));
        Raise(nameof(SizeText));
        Raise(nameof(TransferredText));
        Raise(nameof(SpeedText));
        Raise(nameof(EtaText));
        Raise(nameof(CanPause));
        Raise(nameof(CanResume));
        Raise(nameof(CanRestart));
        Raise(nameof(RestartLabel));
        Raise(nameof(CanOpen));
    }

    /// <summary>
    /// Returns whether the segment shape or any segment value changed. The shape
    /// drives the summary text; the content drives the chunk bar.
    /// </summary>
    private (bool ShapeChanged, bool ContentChanged) UpdateSegmentProgresses(
        IReadOnlyList<DownloadSegmentProgress>? progressSegments)
    {
        if (progressSegments is null)
        {
            return (false, false);
        }

        var ordered = progressSegments.OrderBy(segment => segment.Index).ToArray();
        var shapeChanged = _segmentProgresses.Count != ordered.Length ||
                           _segmentProgresses.Where((segment, index) =>
                               segment.Index != ordered[index].Index ||
                               segment.TotalBytes != ordered[index].TotalBytes).Any();
        var contentChanged = shapeChanged ||
                             _segmentProgresses.Where((segment, index) =>
                                 !segment.Equals(ordered[index])).Any();

        // One snapshot per tick instead of one collection notification per
        // segment: a long media playlist can carry thousands of parts.
        _segmentProgresses = ordered;
        return (shapeChanged, contentChanged);
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatEta(TimeSpan eta)
    {
        if (eta <= TimeSpan.Zero)
        {
            return "—";
        }

        if (eta.TotalDays >= 1)
        {
            return $"{(int)eta.TotalDays}天 {eta.Hours:00}:{eta.Minutes:00}";
        }

        return eta.TotalHours >= 1
            ? $"{(int)eta.TotalHours}:{eta.Minutes:00}:{eta.Seconds:00}"
            : $"{eta.Minutes:00}:{eta.Seconds:00}";
    }
}
