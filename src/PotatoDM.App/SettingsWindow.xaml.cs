using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

        VersionText.Text = "版本 " + (typeof(App).Assembly.GetName().Version?.ToString() ?? "0.1.0");
        Loaded += (_, _) => RefreshBrowserStatus();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBar(this);
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

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralPanel is null || DownloadPanel is null || BrowserPanel is null || AboutPanel is null ||
            SaveButton is null || CancelButton is null)
        {
            return;
        }

        var index = SectionList.SelectedIndex;
        GeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        BrowserPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        var settingsTab = index <= 1;
        SaveButton.Visibility = settingsTab ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = settingsTab ? "取消(_C)" : "关闭(_C)";

        if (index == 2)
        {
            RefreshBrowserStatus();
        }
    }

    private void RefreshBrowserStatus()
    {
        if (BrowserStatusText is null)
        {
            return;
        }

        var status = BrowserIntegrationInstaller.GetStatus();
        var ready = status.FullyInstalled;
        var partiallyInstalled = status.HostPresent && status.SourcePresent;

        BrowserStatusPillText.Text = ready ? "已就绪" : partiallyInstalled ? "待注册" : "未安装";
        SetPill(
            BrowserStatusPill,
            BrowserStatusPillText,
            ready ? PillState.Ok : PillState.Attention);

        BrowserStatusText.Text = ready
            ? "扩展已就绪，在浏览器中加载一次即可使用。"
            : partiallyInstalled
                ? "宿主与扩展已复制，点击“安装 / 修复”完成注册。"
                : "尚未安装浏览器接管。";

        var detected = new List<string>();
        if (status.ChromeDetected) detected.Add("Chrome");
        if (status.EdgeDetected) detected.Add("Edge");
        BrowserDetectedText.Text = detected.Count == 0
            ? "未检测到 Chrome 或 Edge。"
            : "已检测到 " + string.Join(" / ", detected) + "。";

        BrowserStepText.Text = "安装后，在浏览器扩展页开启开发者模式，加载“扩展目录”中的文件夹。";
        BrowserStepText.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;

        var elevated = IsElevated();
        BrowserWarningText.Text = "当前以管理员身份运行，浏览器扩展无法连接；请退出后以普通用户启动。";
        BrowserWarningText.Visibility = elevated ? Visibility.Visible : Visibility.Collapsed;

        ChromeButton.IsEnabled = status.ChromeDetected;
        EdgeButton.IsEnabled = status.EdgeDetected;

        ExtensionIdBox.Text = status.ChromeId ?? "（安装后生成）";
        ChromiumPathBox.Text = status.ChromiumExtensionPath;
        ManifestPathBox.Text = status.HostManifestPath;
    }

    private static void SetPill(Border pill, TextBlock text, PillState state)
    {
        var (backgroundKey, foregroundKey) = state switch
        {
            PillState.Ok => ("StateSuccessBackgroundBrush", "StateSuccessForegroundBrush"),
            _ => ("StateWarningBackgroundBrush", "StateWarningForegroundBrush"),
        };

        pill.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        text.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
    }

    private void OnBrowserInstall(object sender, RoutedEventArgs e) =>
        Run(BrowserIntegrationInstaller.Install, "安装完成。打开浏览器扩展页即可加载扩展。");

    private void OnBrowserUninstall(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "移除宿主注册并删除已复制的扩展？浏览器中已经加载的扩展需要手动移除。",
            "土豆下载器",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            Run(BrowserIntegrationInstaller.Uninstall, "已移除浏览器集成。");
        }
    }

    private void OnOpenChrome(object sender, RoutedEventArgs e) =>
        Run(() => BrowserIntegrationInstaller.OpenExtensionPage(BrowserKind.Chrome), null);

    private void OnOpenEdge(object sender, RoutedEventArgs e) =>
        Run(() => BrowserIntegrationInstaller.OpenExtensionPage(BrowserKind.Edge), null);

    private void OnOpenExtensionFolder(object sender, RoutedEventArgs e) =>
        Run(BrowserIntegrationInstaller.OpenExtensionFolder, null);

    private void OnCopyExtensionId(object sender, RoutedEventArgs e) =>
        Copy(ExtensionIdBox.Text, "扩展 ID 已复制。");

    private void OnCopyChromiumPath(object sender, RoutedEventArgs e) =>
        Copy(ChromiumPathBox.Text, "扩展目录已复制。");

    private void OnCopyManifestPath(object sender, RoutedEventArgs e) =>
        Copy(ManifestPathBox.Text, "宿主清单路径已复制。");

    private void Copy(string text, string message)
    {
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith('（'))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            BrowserStatusText.Text = message;
        }
        catch (Exception ex)
        {
            ShowWarning(ex.Message);
        }
    }

    private void Run(Action action, string? successMessage)
    {
        try
        {
            action();

            if (!string.IsNullOrEmpty(successMessage))
            {
                MessageBox.Show(this, successMessage, "土豆下载器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ShowWarning(ex.Message);
        }

        RefreshBrowserStatus();
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private void ShowWarning(string message) =>
        MessageBox.Show(this, message, "土豆下载器", MessageBoxButton.OK, MessageBoxImage.Warning);

    private enum PillState
    {
        Attention,
        Ok,
    }
}
