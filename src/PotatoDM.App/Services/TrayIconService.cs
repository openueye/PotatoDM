using System.Drawing;
using Forms = System.Windows.Forms;

namespace PotatoDM.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon? _ownedIcon;
    private Action? _notificationClick;

    public TrayIconService(
        Action open,
        Action add,
        Func<Task> pauseAll,
        Func<Task> resumeAll,
        Func<Task> exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开土豆下载器", null, (_, _) => open());
        menu.Items.Add("新建下载", null, (_, _) => add());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("暂停全部", null, async (_, _) => await pauseAll());
        menu.Items.Add("继续全部", null, async (_, _) => await resumeAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await exit());

        _ownedIcon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Icon.ExtractAssociatedIcon(Environment.ProcessPath)
            : null;

        _icon = new Forms.NotifyIcon
        {
            Text = "土豆下载器",
            Icon = _ownedIcon ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => _notificationClick?.Invoke();
    }

    public void Notify(string title, string message, Action onClick)
    {
        _notificationClick = onClick;
        _icon.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Info);
    }

    /// <summary>Mirrors the main window's status line into the tray tooltip.</summary>
    public void SetStatus(string statusText)
    {
        var text = Truncate(
            string.IsNullOrWhiteSpace(statusText) ? "土豆下载器" : statusText.Trim(),
            63);

        if (string.Equals(_icon.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        _icon.Text = text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon?.Dispose();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
