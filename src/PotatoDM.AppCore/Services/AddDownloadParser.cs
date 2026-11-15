using PotatoDM.Torrent;

namespace PotatoDM.AppCore.Services;

/// <summary>Splits and validates the URLs/magnets pasted into the add dialog.</summary>
public static class AddDownloadParser
{
    public static bool TryParse(
        string? text,
        out IReadOnlyList<string> sources,
        out string? error)
    {
        sources = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请输入下载地址。";
            return false;
        }

        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
        {
            error = "请输入下载地址。";
            return false;
        }

        var parsed = new List<string>(lines.Length);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];

            if (TorrentSource.IsMagnet(line))
            {
                try
                {
                    TorrentSource.Validate(line);
                }
                catch (TorrentException ex)
                {
                    error = Prefix(index, lines.Length, ex.Message);
                    return false;
                }

                parsed.Add(line);
                continue;
            }

            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = Prefix(
                    index,
                    lines.Length,
                    "请输入 HTTP/HTTPS 下载地址或 magnet 磁力链接。");
                return false;
            }

            parsed.Add(uri.ToString());
        }

        sources = parsed;
        error = null;
        return true;
    }

    private static string Prefix(int index, int count, string message) =>
        count > 1 ? $"第 {index + 1} 行：{message}" : message;
}
