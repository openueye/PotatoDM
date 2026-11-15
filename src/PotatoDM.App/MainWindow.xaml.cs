using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PotatoDM.App.Services;
using PotatoDM.App.ViewModels;
using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Torrent;

namespace PotatoDM.App;

public partial class MainWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly MainViewModel _viewModel;
    private readonly Func<Task>? _exitApplication;

    public MainWindow(
        DownloadManager manager,
        AppSettings settings,
        SettingsStore settingsStore,
        Func<Task>? exitApplication = null)
    {
        InitializeComponent();

        _manager = manager;
        _settings = settings;
        _settingsStore = settingsStore;
        _exitApplication = exitApplication;
        _viewModel = new MainViewModel(manager, ReportError);
        _viewModel.AddRequested += OnAddRequested;
        _viewModel.RemoveRequested += OnRemoveRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        DataContext = _viewModel;

        // InputBindings are not in the visual tree, so a XAML {Binding} there
        // never sees the window's DataContext; bind the commands directly.
        InputBindings.Add(new KeyBinding(
            _viewModel.AddCommand, Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(
            _viewModel.RemoveCommand, Key.Delete, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(
            _viewModel.TogglePauseCommand, Key.Space, ModifierKeys.None));
    }

    /// <summary>Raised when the tray icon's tooltip should follow the status line.</summary>
    public event Action<string>? StatusTextChanged;

    public string StatusText => _viewModel.StatusText;

    private async void OnAddRequested(object? sender, EventArgs e)
        => await ShowAddDownloadDialogAsync();

    public void OpenAddDownloadDialog()
        => _ = ShowAddDownloadDialogAsync();

    private async Task ShowAddDownloadDialogAsync()
    {
        var dialog = new AddDownloadWindow(_settings) { Owner = this };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings.AutomaticConnections = dialog.Connections == 0;
        if (dialog.Connections > 0)
        {
            _settings.Connections = dialog.Connections;
        }

        // Remember the folder the user actually chose, not just the connection count.
        _settings.DownloadFolder = dialog.Folder;

        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            ReportError("保存设置失败：" + ex.Message);
        }

        var failures = new List<string>();

        foreach (var source in dialog.Sources)
        {
            try
            {
                if (TorrentSource.IsMagnet(source))
                {
                    await _manager.AddTorrentAsync(source, dialog.Folder);
                }
                else
                {
                    await _manager.AddAsync(new Uri(source), dialog.Folder, dialog.Connections);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{source}\n  {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            ReportError("部分下载添加失败：\n\n" + string.Join("\n\n", failures));
        }
    }

    private async void OnRemoveRequested(object? sender, EventArgs e)
    {
        var jobs = _viewModel.SelectedJobs;

        if (jobs.Count == 0)
        {
            return;
        }

        var message = jobs.Count == 1
            ? jobs[0].State == DownloadJobState.Completed
                ? $"从列表中移除“{jobs[0].DisplayName}”，并清理该任务的过程文件？已下载的文件会保留。"
                : $"移除“{jobs[0].DisplayName}”？整个任务文件夹（含未完成的数据）将被删除。"
            : $"移除选中的 {jobs.Count} 个任务？已完成的任务只清理过程文件；未完成的任务整个文件夹会被删除。";

        var answer = MessageBox.Show(
            this,
            message,
            "土豆下载器",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var job in jobs.ToArray())
            {
                await _manager.RemoveAsync(job, deletePartialData: job.State != DownloadJobState.Completed);
            }
        }
        catch (Exception ex)
        {
            ReportError("移除任务失败：" + ex.Message);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The grid can raise this while InitializeComponent is still running,
        // before DataContext (and therefore the view model) is attached.
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SetSelection(JobsGrid.SelectedItems.OfType<DownloadJob>().ToArray());
        }
    }

    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-clicking a row must act on that row, not on the previous selection.
        if (ItemsControl.ContainerFromElement(JobsGrid, e.OriginalSource as DependencyObject)
            is DataGridRow row)
        {
            JobsGrid.SelectedItem = row.Item;
        }
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedJob is not { } job)
        {
            return;
        }

        if (job.CanOpen)
        {
            _viewModel.OpenFileCommand.Execute(null);
            return;
        }

        if (job.State == DownloadJobState.Failed)
        {
            MessageBox.Show(
                this,
                string.IsNullOrWhiteSpace(job.Error) ? "该任务已失败。" : job.Error,
                "下载失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // DataGrid handles Space for selection, so intercept it before the grid
        // gets the key; text fields must still receive ordinary spaces.
        if (e.Key == Key.Space &&
            Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase &&
            _viewModel.TogglePauseCommand.CanExecute(null))
        {
            _viewModel.TogglePauseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StatusText))
        {
            StatusTextChanged?.Invoke(_viewModel.StatusText);
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (((App)Application.Current).IsExiting)
        {
            return;
        }

        e.Cancel = true;
        if (_settings.CloseToTray)
        {
            ((App)Application.Current).HideToTray();
            return;
        }

        _ = _exitApplication?.Invoke();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeGlyph is null || MaxRestoreButton is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        MaxRestoreButton.ToolTip = maximized ? "还原" : "最大化";
        System.Windows.Automation.AutomationProperties.SetName(
            MaxRestoreButton,
            maximized ? "还原" : "最大化");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var round = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));

            var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            _ = DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Older Windows builds do not have the Windows 11 corner attribute.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size);

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await _settingsStore.SaveAsync(_settings);
                StartupRegistration.SetEnabled(_settings.LaunchAtStartup);
                ThemeManager.Apply(_settings.Theme);
            }
            catch (Exception ex)
            {
                ReportError("保存设置失败：" + ex.Message);
            }

            _manager.RefreshQueue();
        }
    }

    private void ReportError(string message)
    {
        MessageBox.Show(
            this,
            message,
            "土豆下载器",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
