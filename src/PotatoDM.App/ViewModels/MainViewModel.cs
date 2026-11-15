using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.Core.Formatting;

namespace PotatoDM.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly DownloadManager _manager;
    private readonly Action<string>? _reportError;
    private DownloadJob? _selectedJob;
    private IReadOnlyList<DownloadJob> _selectedJobs = Array.Empty<DownloadJob>();
    private string _statusText = "就绪";
    private string _searchText = string.Empty;
    private JobFilter _filter = JobFilter.All;
    private DateTimeOffset _lastStatusRefresh;

    public MainViewModel(DownloadManager manager, Action<string>? reportError = null)
    {
        _manager = manager;
        _reportError = reportError;

        FilteredJobs = CollectionViewSource.GetDefaultView(_manager.Jobs);
        FilteredJobs.Filter = item =>
            item is DownloadJob job && JobFiltering.Matches(job, _searchText, _filter);

        AddCommand = new RelayCommand(() => AddRequested?.Invoke(this, EventArgs.Empty));
        PauseCommand = CreateAsync(
            PauseAsync,
            () => ActiveJobs.Any(job => job.CanPause));
        ResumeCommand = CreateAsync(
            ResumeAsync,
            () => ActiveJobs.Any(job => job.CanResume));
        RestartCommand = CreateAsync(
            RestartAsync,
            () => ActiveJobs.Any(job => job.CanRestart));
        TogglePauseCommand = CreateAsync(
            TogglePauseAsync,
            () => ActiveJobs.Any(job => job.CanPause || job.CanResume));
        RemoveCommand = new RelayCommand(
            () => RemoveRequested?.Invoke(this, EventArgs.Empty),
            () => ActiveJobs.Count > 0);
        OpenFileCommand = new RelayCommand(
            OpenFile,
            () => SelectedJob?.CanOpen == true);
        OpenFolderCommand = new RelayCommand(
            OpenFolder,
            () => SelectedJob is not null && !string.IsNullOrEmpty(SelectedJob.TargetPath));
        CopyUrlCommand = new RelayCommand(
            CopyUrl,
            () => SelectedJob is not null);

        ((INotifyCollectionChanged)_manager.Jobs).CollectionChanged += OnJobsChanged;

        foreach (var job in _manager.Jobs)
        {
            job.PropertyChanged += OnJobChanged;
        }

        UpdateStatus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? AddRequested;

    public event EventHandler? RemoveRequested;

    public ReadOnlyObservableCollection<DownloadJob> Jobs => _manager.Jobs;

    public ICollectionView FilteredJobs { get; }

    public IReadOnlyList<KeyValuePair<JobFilter, string>> Filters { get; } = new[]
    {
        new KeyValuePair<JobFilter, string>(JobFilter.All, "全部"),
        new KeyValuePair<JobFilter, string>(JobFilter.Downloading, "下载中"),
        new KeyValuePair<JobFilter, string>(JobFilter.Completed, "已完成"),
        new KeyValuePair<JobFilter, string>(JobFilter.Failed, "失败"),
    };

    public IReadOnlyList<DownloadJob> SelectedJobs => _selectedJobs;

    public DownloadJob? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (ReferenceEquals(_selectedJob, value))
            {
                return;
            }

            _selectedJob = value;

            if (value is null)
            {
                if (_selectedJobs.Count <= 1)
                {
                    _selectedJobs = Array.Empty<DownloadJob>();
                }
            }
            else if (!_selectedJobs.Contains(value))
            {
                _selectedJobs = new[] { value };
            }

            Raise(nameof(SelectedJob));
            RaiseCommandStates();
            UpdateStatus();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            var text = value ?? string.Empty;

            if (string.Equals(_searchText, text, StringComparison.Ordinal))
            {
                return;
            }

            _searchText = text;
            Raise(nameof(SearchText));
            RefreshView();
        }
    }

    public JobFilter Filter
    {
        get => _filter;
        set
        {
            if (_filter == value)
            {
                return;
            }

            _filter = value;
            Raise(nameof(Filter));
            RefreshView();
        }
    }

    public bool HasVisibleJobs => !FilteredJobs.IsEmpty;

    public int VisibleCount => FilteredJobs.Cast<object>().Count();

    public string VisibleCountText => $"{VisibleCount} 个任务";

    public string FilterTitle => _filter switch
    {
        JobFilter.Downloading => "下载中",
        JobFilter.Completed => "已完成",
        JobFilter.Failed => "失败",
        _ => "全部下载",
    };

    public string EmptyStateText => _manager.Jobs.Count == 0
        ? "还没有下载任务"
        : "没有匹配的任务";

    public bool HasStatusSummary => _manager.Jobs.Any(job => job.State is
        DownloadJobState.Probing or
        DownloadJobState.Queued or
        DownloadJobState.Downloading or
        DownloadJobState.Paused or
        DownloadJobState.Failed);

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            Raise(nameof(StatusText));
        }
    }

    public ICommand AddCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand ResumeCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand TogglePauseCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand OpenFileCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand CopyUrlCommand { get; }

    /// <summary>Called by the view whenever the grid's multi-selection changes.</summary>
    public void SetSelection(IReadOnlyList<DownloadJob> jobs)
    {
        _selectedJobs = jobs.Count == 0 ? Array.Empty<DownloadJob>() : jobs;

        if (_selectedJobs.Count == 0)
        {
            if (SelectedJob is not null)
            {
                SelectedJob = null;
            }
        }
        else if (!ReferenceEquals(SelectedJob, _selectedJobs[0]))
        {
            SelectedJob = _selectedJobs[0];
        }

        RaiseCommandStates();
        UpdateStatus();
    }

    private IReadOnlyList<DownloadJob> ActiveJobs =>
        _selectedJobs.Count > 0
            ? _selectedJobs
            : SelectedJob is { } job
                ? new[] { job }
                : Array.Empty<DownloadJob>();

    private AsyncRelayCommand CreateAsync(Func<Task> execute, Func<bool>? canExecute)
    {
        var command = new AsyncRelayCommand(execute, canExecute);

        if (_reportError is not null)
        {
            command.ExecutionFailed += ex => _reportError(ex.Message);
        }

        return command;
    }

    private async Task PauseAsync()
    {
        foreach (var job in ActiveJobs.Where(job => job.CanPause).ToArray())
        {
            await _manager.PauseAsync(job);
        }
    }

    private async Task ResumeAsync()
    {
        foreach (var job in ActiveJobs.Where(job => job.CanResume).ToArray())
        {
            await _manager.ResumeAsync(job);
        }
    }

    private async Task RestartAsync()
    {
        foreach (var job in ActiveJobs.Where(job => job.CanRestart).ToArray())
        {
            await _manager.RestartAsync(job);
        }
    }

    private async Task TogglePauseAsync()
    {
        foreach (var job in ActiveJobs.ToArray())
        {
            if (job.CanPause)
            {
                await _manager.PauseAsync(job);
            }
            else if (job.CanResume)
            {
                await _manager.ResumeAsync(job);
            }
        }
    }

    private void CopyUrl()
    {
        var url = SelectedJob?.Url;

        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(url);
        }
        catch (Exception ex) when (
            ex is ExternalException or ArgumentException or ThreadStateException)
        {
            _reportError?.Invoke("无法复制链接：" + ex.Message);
        }
    }

    private void OpenFile()
    {
        var path = SelectedJob?.TargetPath;

        if (string.IsNullOrEmpty(path))
        {
            _reportError?.Invoke("文件不存在或尚未下载完成。");
            return;
        }

        if (!File.Exists(path))
        {
            _reportError?.Invoke("文件不存在或尚未下载完成。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _reportError?.Invoke("无法打开文件：" + ex.Message);
        }
    }

    private void OpenFolder()
    {
        var path = SelectedJob?.TargetPath;

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var target = File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{Path.GetDirectoryName(path)}\"";

            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _reportError?.Invoke("无法打开文件夹：" + ex.Message);
        }
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var job in e.OldItems.OfType<DownloadJob>())
            {
                job.PropertyChanged -= OnJobChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var job in e.NewItems.OfType<DownloadJob>())
            {
                job.PropertyChanged += OnJobChanged;
            }
        }

        _selectedJobs = _selectedJobs.Where(_manager.Jobs.Contains).ToArray();

        if (SelectedJob is not null && !_manager.Jobs.Contains(SelectedJob))
        {
            SelectedJob = null;
        }

        RaiseVisibleState();
        RaiseCommandStates();
        UpdateStatus();
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Command availability only changes with state, so byte-level progress
            // must not re-query six commands on every tick.
            case nameof(DownloadJob.State):
            case nameof(DownloadJob.CanPause):
            case nameof(DownloadJob.CanResume):
            case nameof(DownloadJob.CanRestart):
            case nameof(DownloadJob.CanOpen):
                if (_filter != JobFilter.All || !string.IsNullOrWhiteSpace(_searchText))
                {
                    FilteredJobs.Refresh();
                }

                RaiseVisibleState();
                RaiseCommandStates();
                UpdateStatus();
                break;
            case nameof(DownloadJob.BytesPerSecond):
            case nameof(DownloadJob.BytesDownloaded):
                UpdateStatusIfDue();
                break;
        }
    }

    private void UpdateStatusIfDue()
    {
        var now = DateTimeOffset.UtcNow;

        if (now - _lastStatusRefresh < StatusRefreshInterval)
        {
            return;
        }

        _lastStatusRefresh = now;
        UpdateStatus();
    }

    private void RefreshView()
    {
        FilteredJobs.Refresh();
        RaiseVisibleState();
    }

    private void RaiseVisibleState()
    {
        Raise(nameof(HasVisibleJobs));
        Raise(nameof(VisibleCount));
        Raise(nameof(VisibleCountText));
        Raise(nameof(FilterTitle));
        Raise(nameof(EmptyStateText));
        Raise(nameof(HasStatusSummary));
    }

    private void RaiseCommandStates()
    {
        (PauseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ResumeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RestartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (TogglePauseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RemoveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyUrlCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void UpdateStatus()
    {
        var active = _manager.Jobs.Count(job => job.State == DownloadJobState.Downloading);
        var queued = _manager.Jobs.Count(job => job.State == DownloadJobState.Queued);
        var paused = _manager.Jobs.Count(job => job.State == DownloadJobState.Paused);
        var speed = _manager.Jobs
            .Where(job => job.State == DownloadJobState.Downloading)
            .Sum(job => job.BytesPerSecond);

        if (_manager.Jobs.Count == 0)
        {
            StatusText = "就绪";
            return;
        }

        var parts = new List<string>();

        if (active > 0)
        {
            parts.Add($"{active} 个任务下载中 · {ByteSize.FormatSpeed(speed)}");
        }

        if (queued > 0)
        {
            parts.Add($"{queued} 个排队中");
        }

        if (paused > 0)
        {
            parts.Add($"{paused} 个已暂停");
        }

        if (parts.Count == 0)
        {
            parts.Add($"共 {_manager.Jobs.Count} 个任务");
        }

        StatusText = string.Join(" · ", parts);
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
