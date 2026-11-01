using System.IO;
using System.Windows;
using System.Windows.Threading;
using PotatoDM.App.Services;
using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.BrowserIpc;
using PotatoDM.Core.Diagnostics;

namespace PotatoDM.App;

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
