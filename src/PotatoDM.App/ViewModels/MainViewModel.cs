using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.Core.Formatting;

namespace PotatoDM.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly DownloadManager _manager;
    private readonly Action<string>? _reportError;
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromMilliseconds(250);

    private DownloadJob? _selectedJob;
    private string _statusText = "就绪";
    private DateTimeOffset _lastStatusRefresh;

    public MainViewModel(DownloadManager manager, Action<string>? reportError = null)
    {
        _manager = manager;
        _reportError = reportError;

        AddCommand = new RelayCommand(() => AddRequested?.Invoke(this, EventArgs.Empty));
        PauseCommand = CreateAsync(
            PauseAsync,
            () => SelectedJob?.CanPause == true);
        ResumeCommand = CreateAsync(
            ResumeAsync,
            () => SelectedJob?.CanResume == true);
        RestartCommand = CreateAsync(
            RestartAsync,
            () => SelectedJob?.CanRestart == true);
        RemoveCommand = new RelayCommand(
            () => RemoveRequested?.Invoke(this, EventArgs.Empty),
            () => SelectedJob is not null);
        OpenFileCommand = new RelayCommand(
            OpenFile,
            () => SelectedJob?.CanOpen == true);
        OpenFolderCommand = new RelayCommand(
            OpenFolder,
            () => SelectedJob is not null && !string.IsNullOrEmpty(SelectedJob.TargetPath));

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
            Raise(nameof(SelectedJob));
            RaiseCommandStates();
            UpdateStatus();
        }
    }

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

    public ICommand RemoveCommand { get; }

    public ICommand OpenFileCommand { get; }

    public ICommand OpenFolderCommand { get; }

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
        if (SelectedJob is { } job)
        {
            await _manager.PauseAsync(job);
        }
    }

    private async Task ResumeAsync()
    {
        if (SelectedJob is { } job)
        {
            await _manager.ResumeAsync(job);
        }
    }

    private async Task RestartAsync()
    {
        if (SelectedJob is { } job)
        {
            await _manager.RestartAsync(job);
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

        if (SelectedJob is not null && !_manager.Jobs.Contains(SelectedJob))
        {
            SelectedJob = null;
        }

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

    private void RaiseCommandStates()
    {
        (PauseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ResumeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RestartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RemoveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
