using System.IO;
using System.Windows;
using Microsoft.Win32;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Torrent;

namespace PotatoDM.App;

public partial class AddDownloadWindow : Window
{
    public AddDownloadWindow(AppSettings settings)
    {
        InitializeComponent();

        var choices = new[]
        {
            new KeyValuePair<int, string>(0, "自动（推荐）"),
            new KeyValuePair<int, string>(1, "1"),
            new KeyValuePair<int, string>(2, "2"),
            new KeyValuePair<int, string>(4, "4"),
            new KeyValuePair<int, string>(8, "8"),
            new KeyValuePair<int, string>(16, "16"),
            new KeyValuePair<int, string>(32, "32"),
        };
        ConnectionsBox.ItemsSource = choices;
        ConnectionsBox.SelectedValue = settings.AutomaticConnections ? 0 : settings.Connections;

        FolderBox.Text = string.IsNullOrWhiteSpace(settings.DownloadFolder)
            ? AppPaths.DefaultDownloadFolder
            : settings.DownloadFolder;

        Loaded += (_, _) => UrlBox.Focus();
    }

    public string Source { get; private set; } = string.Empty;

    public bool IsMagnet { get; private set; }

    public string Folder => FolderBox.Text.Trim();

    public int Connections => ConnectionsBox.SelectedValue is int value ? value : 0;

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择保存位置",
            Multiselect = false,
        };

        var current = FolderBox.Text.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        var text = UrlBox.Text.Trim();

        if (TorrentSource.IsMagnet(text))
        {
            try
            {
                TorrentSource.Validate(text);
            }
            catch (TorrentException ex)
            {
                ShowError(ex.Message);
                UrlBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(FolderBox.Text))
            {
                ShowError("请选择保存位置。");
                FolderBox.Focus();
                return;
            }

            Source = text;
            IsMagnet = true;
            DialogResult = true;
            return;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            ShowError("请输入 HTTP/HTTPS 下载地址或 magnet 磁力链接。");
            UrlBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            ShowError("请选择保存位置。");
            FolderBox.Focus();
            return;
        }

        Source = url.ToString();
        IsMagnet = false;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
