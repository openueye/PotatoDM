using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PotatoDM.App.Services;
using PotatoDM.App.ViewModels;
using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;

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

        DataContext = _viewModel;
    }

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
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            ReportError("保存设置失败：" + ex.Message);
        }

        try
        {
            if (dialog.IsMagnet)
            {
                await _manager.AddTorrentAsync(dialog.Source, dialog.Folder);
            }
            else
            {
                await _manager.AddAsync(new Uri(dialog.Source), dialog.Folder, dialog.Connections);
            }
        }
        catch (Exception ex)
        {
            ReportError("无法添加下载：" + ex.Message);
        }
    }

    private async void OnRemoveRequested(object? sender, EventArgs e)
    {
        var job = _viewModel.SelectedJob;

        if (job is null)
        {
            return;
        }

        var completed = job.State == DownloadJobState.Completed;
        var message = completed
            ? $"从列表中移除“{job.DisplayName}”，并清理该任务的过程文件？已下载的文件会保留。"
            : $"移除“{job.DisplayName}”？整个任务文件夹（含未完成的数据）将被删除。";

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

        await _manager.RemoveAsync(job, deletePartialData: !completed);
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedJob is { CanOpen: true })
        {
            _viewModel.OpenFileCommand.Execute(null);
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

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await _settingsStore.SaveAsync(_settings);
                StartupRegistration.SetEnabled(_settings.LaunchAtStartup);
            }
            catch (Exception ex)
            {
                ReportError("保存设置失败：" + ex.Message);
            }

            _manager.RefreshQueue();
        }
    }

    private void OnBrowserIntegration(object sender, RoutedEventArgs e)
    {
        var window = new BrowserIntegrationWindow { Owner = this };
        window.ShowDialog();
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
