using System.Collections.ObjectModel;
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
    private string? _error;
    private bool _hasStateFile;
    private readonly ObservableCollection<DownloadSegmentProgress> _segmentProgresses = [];

    public DownloadJob(string id, string url, DateTimeOffset createdUtc)
    {
        Id = id;
        Url = url;
        CreatedUtc = createdUtc;
        SegmentProgresses = new ReadOnlyObservableCollection<DownloadSegmentProgress>(_segmentProgresses);
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

    public bool HasStateFile => _hasStateFile;

    /// <summary>Read-only snapshots for the visual mini-bars under the total progress.</summary>
    public ReadOnlyObservableCollection<DownloadSegmentProgress> SegmentProgresses { get; }

    public bool HasSegmentProgress => SegmentProgresses.Count > 1;

    public string SegmentSummary =>
        !HasSegmentProgress
            ? string.Empty
            : IsTorrent
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
        && _state is DownloadJobState.Failed or DownloadJobState.Paused;

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(_displayName)
            ? _displayName
            : string.IsNullOrEmpty(_targetPath)
                ? Url
                : Path.GetFileName(_targetPath);

    public double PercentValue =>
        _totalBytes > 0
            ? Math.Clamp((double)_bytesDownloaded / _totalBytes * 100.0, 0, 100)
            : 0;

    public string ProgressText =>
        _totalBytes > 0 ? $"{PercentValue:0.0}%" : "—";

    public string SizeText =>
        _totalBytes > 0 ? ByteSize.Format(_totalBytes) : "—";

    public string TransferredText =>
        _torrentChecking
            ? $"校验进度 {PercentValue:0.0}%"
            : _totalBytes > 0
                ? $"{ByteSize.Format(_bytesDownloaded)} / {ByteSize.Format(_totalBytes)}"
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
        DownloadJobState.Downloading => _torrentChecking ? "校验中" : "下载中",
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

    public void MarkQueued()
    {
        _state = DownloadJobState.Queued;
        _bytesPerSecond = 0;
        _eta = null;
        _activeConnections = 0;
        _torrentChecking = false;
        _error = null;
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

    public void ApplyProgress(DownloadProgress progress)
    {
        _bytesDownloaded = Math.Max(0, progress.BytesDownloaded);

        if (progress.TotalBytes is { } total && total >= 0)
        {
            _totalBytes = total;
        }

        _bytesPerSecond = progress.BytesPerSecond;
        _eta = progress.Eta;
        _activeConnections = progress.ActiveConnections;
        UpdateSegmentProgresses(progress.Segments);
        RaiseAll();
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

    public void MarkFailed(string message)
    {
        _bytesPerSecond = 0;
        _eta = null;
        _activeConnections = 0;
        _error = message;
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
        Raise(nameof(TargetPath));
        Raise(nameof(StatePath));
        Raise(nameof(DisplayName));
        Raise(nameof(TotalBytes));
        Raise(nameof(BytesDownloaded));
        Raise(nameof(BytesPerSecond));
        Raise(nameof(Eta));
        Raise(nameof(ActiveConnections));
        Raise(nameof(Error));
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
        Raise(nameof(CanOpen));
    }

    private void UpdateSegmentProgresses(IReadOnlyList<DownloadSegmentProgress>? progressSegments)
    {
        if (progressSegments is null)
        {
            return;
        }

        var ordered = progressSegments.OrderBy(segment => segment.Index).ToArray();
        var shapeChanged = _segmentProgresses.Count != ordered.Length ||
                           _segmentProgresses.Where((segment, index) => segment.Index != ordered[index].Index ||
                               segment.TotalBytes != ordered[index].TotalBytes).Any();

        if (shapeChanged)
        {
            _segmentProgresses.Clear();
            foreach (var segment in ordered)
            {
                _segmentProgresses.Add(segment);
            }

            return;
        }

        for (var index = 0; index < ordered.Length; index++)
        {
            _segmentProgresses[index] = ordered[index];
        }
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
