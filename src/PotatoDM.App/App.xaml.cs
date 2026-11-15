using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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

            _manager = new DownloadManager(
                new DispatcherUiDispatcher(Dispatcher),
                new JobRegistryStore(log: _log),
                new DownloadEngineFactory(_log),
                settings,
                new DpapiCookieProtector(),
                log: _log);

            await _manager.InitializeAsync();

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
            _manager.JobCompleted += OnJobCompleted;
            _manager.JobFailed += OnJobFailed;

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
            await StopBrowserHandoffAsync();
            if (_manager is not null)
            {
                await _manager.PauseAllAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            Shutdown(0);
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

    private void OnJobCompleted(object? sender, DownloadJob job)
    {
        _trayIcon?.Notify("下载完成", job.DisplayName, isError: false, ActivateWindow);
    }

    private void OnJobFailed(object? sender, DownloadJob job)
    {
        var detail = string.IsNullOrWhiteSpace(job.Error)
            ? job.DisplayName
            : $"{job.DisplayName}\n{job.Error}";

        _trayIcon?.Notify("下载失败", detail, isError: true, ActivateWindow);
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
