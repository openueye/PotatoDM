using System.Drawing;
using Forms = System.Windows.Forms;

namespace PotatoDM.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon? _ownedIcon;
    private Action? _balloonAction;

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
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };
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

    /// <summary>Shows a completion/failure balloon; clicking it runs <paramref name="onClick"/>.</summary>
    public void Notify(string title, string message, bool isError, Action? onClick = null)
    {
        _balloonAction = onClick;
        _icon.BalloonTipTitle = Truncate(title, 63);
        _icon.BalloonTipText = Truncate(message, 255);
        _icon.BalloonTipIcon = isError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
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
