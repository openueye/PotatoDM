using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Win32;
using PotatoDM.App.Services;
using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.BrowserIpc;
using PotatoDM.Core.Diagnostics;

namespace PotatoDM.App;

// WPF owns the Application lifetime and has no disposal hook; OnExit releases
// these fields explicitly, so there is no path on which Dispose could be called.
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Released in OnExit; Application has no disposal hook.")]
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\PotatoDM.SingleInstance.v1";

    // Replaced in OnStartup once the log sink exists: a damaged settings.json must
    // not be reported to a sink that does not exist yet.
    private SettingsStore _settingsStore = new();
    private Mutex? _singleInstance;
    private BrowserPipeServer? _pipeServer;
    private DownloadManager? _manager;
    private MainWindow? _window;
    private CancellationTokenSource? _lifetime;
    private TrayIconService? _trayIcon;
    private FileLogSink? _log;
    private AppSettings? _settings;
    private bool _themeEventsSubscribed;
    private bool _isExiting;
    private readonly Dictionary<DownloadJob, DownloadJobState> _notificationJobs = new();
    private readonly Dictionary<DownloadJob, DownloadJobState> _notificationStates = new();
    private DispatcherTimer? _notificationTimer;

    public bool IsExiting => _isExiting;
    private int _handoffStopped;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => Log("DISPATCHER: " + args.Exception);

        if (!TryAcquireSingleInstance())
        {
            // Another instance owns the app; ask it to surface, then leave.
            await TryActivateRunningInstanceAsync();
            Shutdown(0);
            return;
        }

        try
        {
            _lifetime = new CancellationTokenSource();

            // Startup failures keep going to startup.log (this sink is not up yet and
            // must not depend on the settings file being readable). Everything that
            // happens after the manager exists goes through this sink instead.
            _log = new FileLogSink(AppPaths.LogsDirectory);
            _log.Info($"PotatoDM {typeof(App).Assembly.GetName().Version} starting.");

            _settingsStore = new SettingsStore(log: _log);

            var settings = await _settingsStore.LoadAsync();
            _settings = settings;

            ThemeManager.Apply(settings.Theme);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _themeEventsSubscribed = true;

            var registry = new JobRegistryStore(log: _log);
            _manager = new DownloadManager(
                new DispatcherUiDispatcher(Dispatcher),
                registry,
                new DownloadEngineFactory(_log),
                settings,
                new DpapiCookieProtector(),
                log: _log);

            await _manager.InitializeAsync();
            if (registry.DamagedBackupPath is { } backupPath)
            {
                MessageBox.Show(
                    "任务清单损坏或无法读取，无法恢复原有任务。原文件已保存在：\n" + backupPath +
                    "\n\n请保留该文件以便恢复。新任务将保存到新清单。",
                    "任务清单恢复失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (registry.DamagedRegistryUnpreserved)
            {
                MessageBox.Show(
                    "任务清单损坏或无法读取，且无法将其备份。原文件仍位于：\n" + registry.FilePath +
                    "\n\n应用将以空任务列表启动。请先手动备份该文件；在修复前，新任务可能无法保存。",
                    "任务清单恢复失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _window = new MainWindow(
                _manager,
                settings,
                _settingsStore,
                ExitApplicationAsync);

            MainWindow = _window;
            _trayIcon = new TrayIconService(
                ActivateWindow,
                OpenAddDialog,
                () => _manager.PauseAllAsync(TimeSpan.FromSeconds(5)),
                _manager.ResumeAllAsync,
                ExitApplicationAsync);

            _trayIcon.SetStatus(_window.StatusText);
            _window.StatusTextChanged += text => _trayIcon?.SetStatus(text);
            _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _notificationTimer.Tick += OnNotificationTimer;
            foreach (var job in _manager.Jobs) SubscribeJob(job);
            ((INotifyCollectionChanged)_manager.Jobs).CollectionChanged += OnNotificationJobsChanged;

            if (!settings.StartMinimized)
            {
                _window.Show();
            }

            var handler = new BrowserPipeHandler(
                _manager,
                new DispatcherUiDispatcher(Dispatcher),
                ActivateWindow);

            _pipeServer = new BrowserPipeServer(
                BrowserPipeNames.ForCurrentSession(),
                handler.HandleAsync);

            _pipeServer.Start();
        }
        catch (Exception ex)
        {
            Log("startup failed: " + ex);
            MessageBox.Show(
                "启动失败：" + ex.Message,
                "土豆下载器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notificationTimer?.Stop();
        if (_manager is not null)
            ((INotifyCollectionChanged)_manager.Jobs).CollectionChanged -= OnNotificationJobsChanged;
        foreach (var job in _notificationStates.Keys.ToArray()) job.PropertyChanged -= OnNotificationJobChanged;
        if (_themeEventsSubscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _themeEventsSubscribed = false;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        var server = Interlocked.Exchange(ref _pipeServer, null);

        if (server is not null)
        {
            try
            {
                server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;

        if (_singleInstance is not null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _singleInstance.Dispose();
            _singleInstance = null;
        }

        base.OnExit(e);
    }

    private void SubscribeJob(DownloadJob job)
    {
        _notificationStates[job] = job.State;
        job.PropertyChanged += OnNotificationJobChanged;
    }

    private void OnNotificationJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (DownloadJob job in e.OldItems) { job.PropertyChanged -= OnNotificationJobChanged; _notificationStates.Remove(job); }
        if (e.NewItems is not null)
            foreach (DownloadJob job in e.NewItems) SubscribeJob(job);
    }

    private void OnNotificationJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadJob.State) || sender is not DownloadJob job ||
            !_notificationStates.TryGetValue(job, out var previous)) return;
        _notificationStates[job] = job.State;
        if (previous == job.State || job.State is not (DownloadJobState.Completed or DownloadJobState.Failed) ||
            _settings?.ShowNotifications != true) return;
        _notificationJobs[job] = job.State;
        _notificationTimer?.Start();
    }

    private void OnNotificationTimer(object? sender, EventArgs e)
    {
        _notificationTimer?.Stop();
        if (_settings?.ShowNotifications != true || _notificationJobs.Count == 0)
        {
            _notificationJobs.Clear();
            return;
        }
        var jobs = _notificationJobs.ToArray();
        _notificationJobs.Clear();
        var completed = jobs.Count(item => item.Value == DownloadJobState.Completed);
        var failed = jobs.Count(item => item.Value == DownloadJobState.Failed);
        var message = jobs.Length == 1
            ? $"{jobs[0].Key.DisplayName}：{(failed > 0 ? "下载失败" : "下载完成")}"
            : $"{completed} 个任务完成，{failed} 个任务失败。";
        _trayIcon?.Notify("土豆下载器", message, () => _window?.ShowJob(jobs.Length == 1 ? jobs[0].Key : null));
    }

    /// <summary>Stops accepting browser hand-offs; called before pausing on close.</summary>
    public async Task StopBrowserHandoffAsync()
    {
        if (Interlocked.Exchange(ref _handoffStopped, 1) != 0)
        {
            return;
        }

        var server = Interlocked.Exchange(ref _pipeServer, null);

        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }

    public void HideToTray()
    {
        _window?.Hide();
    }

    public async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        try
        {
            if (_manager is not null)
            {
                await _manager.PauseAllAsync(TimeSpan.FromSeconds(5));
                if (_manager.RegistrySaveFailed && MessageBox.Show(
                    "任务列表未能保存，退出后可能丢失。仍要退出吗？", "保存失败",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    _isExiting = false;
                    return;
                }
            }
            await StopBrowserHandoffAsync();
        }
        finally
        {
            if (_isExiting)
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
                Shutdown(0);
            }
        }
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

            if (!createdNew)
            {
                _singleInstance.Dispose();
                _singleInstance = null;
            }

            return createdNew;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // If the mutex cannot be created, running anyway is better than not starting.
            Log("single-instance mutex unavailable: " + ex.Message);
            return true;
        }
    }

    private static async Task TryActivateRunningInstanceAsync()
    {
        try
        {
            await using var client = await BrowserPipeClient.ConnectAsync(
                BrowserPipeNames.ForCurrentSession(),
                TimeSpan.FromSeconds(1));

            await client.SendAsync(
                new BrowserRequest { Type = BrowserRequestTypes.Activate },
                TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (
            ex is IOException or TimeoutException or OperationCanceledException or
            InvalidDataException or UnauthorizedAccessException)
        {
            // The first instance may still be starting; exiting is still correct.
        }
    }

    private void ActivateWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void OpenAddDialog()
    {
        ActivateWindow();
        _window?.OpenAddDownloadDialog();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color) ||
            _settings?.Theme != AppTheme.System ||
            Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => ThemeManager.Apply(AppTheme.System));
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(
                Path.Combine(AppPaths.Root, "startup.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
