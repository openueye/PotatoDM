using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;

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

        TryPrefillFromClipboard();
        Loaded += (_, _) => UrlBox.Focus();
    }

    /// <summary>Validated URLs or magnet links, one per pasted line.</summary>
    public IReadOnlyList<string> Sources { get; private set; } = Array.Empty<string>();

    public string Folder => FolderBox.Text.Trim();

    public int Connections => ConnectionsBox.SelectedValue is int value ? value : 0;

    private void TryPrefillFromClipboard()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                return;
            }

            var text = System.Windows.Clipboard.GetText().Trim();

            // Only prefill something the dialog would accept; an unrelated
            // clipboard should not replace an empty field with noise.
            if (text.Length is > 0 and <= 64 * 1024 &&
                AddDownloadParser.TryParse(text, out _, out _))
            {
                UrlBox.Text = text;
            }
        }
        catch (ExternalException)
        {
            // Another process owns the clipboard; the empty field is fine.
        }
    }

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

        if (!AddDownloadParser.TryParse(UrlBox.Text, out var sources, out var error))
        {
            ShowError(error ?? "请输入下载地址。");
            UrlBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            ShowError("请选择保存位置。");
            FolderBox.Focus();
            return;
        }

        Sources = sources;
        DialogResult = true;
    }

    private void OnUrlKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            OnAccept(sender, e);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
