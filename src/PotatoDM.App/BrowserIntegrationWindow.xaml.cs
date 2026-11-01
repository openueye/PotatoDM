using System.Windows;
using System.Windows.Controls;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using PotatoDM.App.Services;

namespace PotatoDM.App;

public partial class BrowserIntegrationWindow : Window
{
    private static readonly Brush OkBackground = Frozen(0xE7, 0xF4, 0xEC);
    private static readonly Brush OkForeground = Frozen(0x1F, 0x6B, 0x3A);
    private static readonly Brush AttentionBackground = Frozen(0xFD, 0xF0, 0xE6);
    private static readonly Brush AttentionForeground = Frozen(0xA2, 0x55, 0x1F);
    private static readonly Brush NeutralBackground = Frozen(0xF1, 0xF0, 0xEC);
    private static readonly Brush NeutralForeground = Frozen(0x77, 0x71, 0x6A);

    public BrowserIntegrationWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var status = BrowserIntegrationInstaller.GetStatus();

        SetBadge(
            HostBadge,
            HostBadgeText,
            status.HostPresent ? BadgeState.Ok : BadgeState.Attention,
            status.HostPresent ? "已就绪" : "缺失");
        SetBadge(
            SourceBadge,
            SourceBadgeText,
            status.SourcePresent ? BadgeState.Ok : BadgeState.Attention,
            status.SourcePresent ? "存在" : "缺失");

        SetBadge(
            ChromeDetectedBadge,
            ChromeDetectedText,
            status.ChromeDetected ? BadgeState.Ok : BadgeState.Neutral,
            status.ChromeDetected ? "已安装" : "未检测到");
        SetBadge(
            ChromeRegisteredBadge,
            ChromeRegisteredText,
            status.ChromeRegistered ? BadgeState.Ok : BadgeState.Attention,
            status.ChromeRegistered ? "宿主已注册" : "宿主未注册");

        SetBadge(
            EdgeDetectedBadge,
            EdgeDetectedText,
            status.EdgeDetected ? BadgeState.Ok : BadgeState.Neutral,
            status.EdgeDetected ? "已安装" : "未检测到");
        SetBadge(
            EdgeRegisteredBadge,
            EdgeRegisteredText,
            status.EdgeRegistered ? BadgeState.Ok : BadgeState.Attention,
            status.EdgeRegistered ? "宿主已注册" : "宿主未注册");

        var ready = status.FullyInstalled;
        SetBadge(
            OverallBadge,
            OverallBadgeText,
            ready ? BadgeState.Ok : BadgeState.Attention,
            ready ? "待加载扩展" : status.HostPresent && status.SourcePresent ? "待注册" : "未安装");

        StatusLineText.Text = ready
            ? "已安装，打开浏览器扩展页加载扩展即可。"
            : "尚未完整安装，点击“安装 / 修复”完成注册。";

        ExtensionIdBox.Text = status.ChromeId ?? "（安装后生成）";
        ChromiumPathBox.Text = status.ChromiumExtensionPath;
        ManifestPathBox.Text = status.HostManifestPath;

        ChromiumPathBox.ToolTip = status.ChromiumExtensionPath;
        ManifestPathBox.ToolTip = status.HostManifestPath;

        CopyExtensionIdButton.IsEnabled = !string.IsNullOrEmpty(status.ChromeId);
        CopyChromiumPathButton.IsEnabled = !string.IsNullOrWhiteSpace(status.ChromiumExtensionPath);
        CopyManifestPathButton.IsEnabled = !string.IsNullOrWhiteSpace(status.HostManifestPath);
    }

    private static void SetBadge(Border badge, TextBlock label, BadgeState state, string text)
    {
        label.Text = text;

        var (background, foreground) = state switch
        {
            BadgeState.Ok => (OkBackground, OkForeground),
            BadgeState.Attention => (AttentionBackground, AttentionForeground),
            _ => (NeutralBackground, NeutralForeground),
        };

        badge.Background = background;
        label.Foreground = foreground;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        Run(BrowserIntegrationInstaller.Install, "安装完成。打开浏览器扩展页即可加载扩展。");
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "移除宿主注册并删除已复制的扩展？浏览器中已经加载的扩展需要手动移除。",
            "土豆下载器",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        Run(BrowserIntegrationInstaller.Uninstall, "已移除浏览器集成。");
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) =>
        Run(BrowserIntegrationInstaller.OpenExtensionFolder, null);

    private void OnOpenChrome(object sender, RoutedEventArgs e) =>
        Run(() => BrowserIntegrationInstaller.OpenExtensionPage(BrowserKind.Chrome), null);

    private void OnOpenEdge(object sender, RoutedEventArgs e) =>
        Run(() => BrowserIntegrationInstaller.OpenExtensionPage(BrowserKind.Edge), null);

    private void OnCopyExtensionId(object sender, RoutedEventArgs e) =>
        Copy(ExtensionIdBox.Text, "扩展 ID 已复制。");

    private void OnCopyChromiumPath(object sender, RoutedEventArgs e) =>
        Copy(ChromiumPathBox.Text, "扩展目录已复制。");

    private void OnCopyManifestPath(object sender, RoutedEventArgs e) =>
        Copy(ManifestPathBox.Text, "宿主清单路径已复制。");

    private void Copy(string text, string message)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            text.StartsWith('（'))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusLineText.Text = message;
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
                MessageBox.Show(
                    this,
                    successMessage,
                    "土豆下载器",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ShowWarning(ex.Message);
        }

        Refresh();
    }

    private void ShowWarning(string message) =>
        MessageBox.Show(
            this,
            message,
            "土豆下载器",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    private enum BadgeState
    {
        Neutral,
        Attention,
        Ok,
    }
}
