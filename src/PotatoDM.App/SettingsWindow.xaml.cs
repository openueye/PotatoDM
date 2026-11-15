using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.App.Services;

namespace PotatoDM.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ConnectionsBox.ItemsSource = new[] { 1, 2, 4, 8, 16, 32 };
        ConnectionsBox.SelectedItem = settings.Connections;

        var jobLimits = new List<int> { 1, 2, 3, 4, 6, 8 };
        var currentJobLimit = Math.Max(1, settings.MaxConcurrentJobs);
        if (!jobLimits.Contains(currentJobLimit))
        {
            jobLimits.Add(currentJobLimit);
            jobLimits.Sort();
        }

        JobLimitBox.ItemsSource = jobLimits;
        JobLimitBox.SelectedItem = currentJobLimit;

        var perJobLimits = new List<int> { 1, 2, 4, 8, 16, 32, AppSettings.MaxConnectionsPerJobCeiling };
        var currentPerJobLimit = Math.Clamp(
            settings.MaxConnectionsPerJob,
            1,
            AppSettings.MaxConnectionsPerJobCeiling);
        if (!perJobLimits.Contains(currentPerJobLimit))
        {
            perJobLimits.Add(currentPerJobLimit);
            perJobLimits.Sort();
        }

        PerJobLimitBox.ItemsSource = perJobLimits;
        PerJobLimitBox.SelectedItem = currentPerJobLimit;

        SpeedLimitBox.Text = ((settings.MaxBytesPerSecond + 512) / 1024)
            .ToString(CultureInfo.InvariantCulture);
        AutomaticBox.IsChecked = settings.AutomaticConnections;
        DynamicSegmentsBox.IsChecked = settings.DynamicSegmentation;
        SparseFileBox.IsChecked = settings.SparseWorkingFile;
        DownloadFolderBox.Text = string.IsNullOrWhiteSpace(settings.DownloadFolder)
            ? AppPaths.DefaultDownloadFolder
            : settings.DownloadFolder;
        FfmpegPathBox.Text = settings.FfmpegPath ?? string.Empty;
        ProxyBox.IsChecked = settings.UseProxy;
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        StartMinimizedBox.IsChecked = settings.StartMinimized;
        LaunchAtStartupBox.IsChecked = settings.LaunchAtStartup;

        ThemeBox.ItemsSource = new[]
        {
            new KeyValuePair<AppTheme, string>(AppTheme.System, "跟随系统"),
            new KeyValuePair<AppTheme, string>(AppTheme.Light, "浅色"),
            new KeyValuePair<AppTheme, string>(AppTheme.Dark, "深色"),
        };
        ThemeBox.SelectedValue = settings.Theme;

        AutomaticBox.Checked += (_, _) => ConnectionsBox.IsEnabled = false;
        AutomaticBox.Unchecked += (_, _) => ConnectionsBox.IsEnabled = true;
        ConnectionsBox.IsEnabled = !settings.AutomaticConnections;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(
                SpeedLimitBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var speedLimitKib) ||
            speedLimitKib < 0 ||
            speedLimitKib > long.MaxValue / 1024)
        {
            MessageBox.Show(
                this,
                "全局限速请输入 0 或正整数（KiB/s）。",
                "土豆下载器",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string downloadFolder;
        try
        {
            downloadFolder = Path.GetFullPath(DownloadFolderBox.Text.Trim());
            Directory.CreateDirectory(downloadFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                "默认下载目录无效或无法创建。",
                "土豆下载器",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _settings.AutomaticConnections = AutomaticBox.IsChecked == true;
        _settings.Connections = ConnectionsBox.SelectedItem is int value ? value : 8;
        _settings.MaxConcurrentJobs = JobLimitBox.SelectedItem is int limit ? limit : 3;
        _settings.MaxConnectionsPerJob = PerJobLimitBox.SelectedItem is int perJobLimit
            ? perJobLimit
            : AppSettings.MaxConnectionsPerJobCeiling;
        _settings.MaxBytesPerSecond = speedLimitKib * 1024;
        _settings.DynamicSegmentation = DynamicSegmentsBox.IsChecked == true;
        _settings.SparseWorkingFile = SparseFileBox.IsChecked == true;
        _settings.DownloadFolder = downloadFolder;
        _settings.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPathBox.Text)
            ? null
            : FfmpegPathBox.Text.Trim();
        _settings.UseProxy = ProxyBox.IsChecked == true;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.StartMinimized = StartMinimizedBox.IsChecked == true;
        _settings.LaunchAtStartup = LaunchAtStartupBox.IsChecked == true;
        _settings.Theme = ThemeBox.SelectedValue is AppTheme theme ? theme : AppTheme.System;
        DialogResult = true;
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "ffmpeg|ffmpeg.exe|可执行文件|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            FfmpegPathBox.Text = dialog.FileName;
        }
    }

    private void OnBrowseDownloadFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择默认下载目录",
            Multiselect = false,
        };

        var current = DownloadFolderBox.Text.Trim();
        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            DownloadFolderBox.Text = dialog.FolderName;
        }
    }

    private void OnCreateDesktopShortcut(object sender, RoutedEventArgs e) => CreateShortcut(
        ShortcutInstaller.CreateDesktopShortcut,
        "桌面快捷方式已创建。");

    private void OnCreateStartMenuShortcut(object sender, RoutedEventArgs e) => CreateShortcut(
        ShortcutInstaller.CreateStartMenuShortcut,
        "开始菜单快捷方式已创建。");

    private void CreateShortcut(Func<string> create, string success)
    {
        try
        {
            create();
            MessageBox.Show(this, success, "土豆下载器", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "创建快捷方式失败：" + ex.Message, "土豆下载器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
