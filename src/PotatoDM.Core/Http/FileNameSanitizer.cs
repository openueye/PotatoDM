using System.Text;

namespace PotatoDM.Core.Http;

/// <summary>Turns server-suggested names into safe, single-component Windows file names.</summary>
public static class FileNameSanitizer
{
    private const int MaxLength = 200;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? name, string fallback = "download.bin")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        // Never allow directory components or path traversal.
        var normalized = name.Replace('\\', '/');
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];

        var builder = new StringBuilder(leaf.Length);
        foreach (var c in leaf)
        {
            if (c >= 32 && "<>:\"/\\|?*".IndexOf(c) < 0)
            {
                builder.Append(c);
            }
        }

        var result = builder.ToString().Trim().TrimEnd(' ', '.');

        if (result.Length == 0 || result == "." || result == "..")
        {
            return fallback;
        }

        var stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedNames.Contains(stem))
        {
            result = "_" + result;
        }

        if (result.Length > MaxLength)
        {
            var extension = Path.GetExtension(result);
            if (extension.Length > 20)
            {
                extension = string.Empty;
            }

            var stemBudget = Math.Max(1, MaxLength - extension.Length);
            var stemValue = Path.GetFileNameWithoutExtension(result);
            if (stemValue.Length > stemBudget)
            {
                stemValue = stemValue[..stemBudget];
            }

            result = stemValue + extension;
        }

        return result;
    }
}
