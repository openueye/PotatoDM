using PotatoDM.Core.Http;
using PotatoDM.Core.Storage;

namespace PotatoDM.AppCore.Job;

/// <summary>Filesystem layout for one job.</summary>
public sealed record JobPaths(
    string TaskDirectory,
    string TargetPath,
    string ProcessDirectory,
    string StatePath);

/// <summary>
/// Every download gets an integrated folder so nothing is scattered in the
/// download root:
/// <code>
/// &lt;download folder&gt;/&lt;name&gt;/&lt;name&gt;.ext
/// &lt;download folder&gt;/&lt;name&gt;/.potatodm/&lt;name&gt;.ext.download.json
/// </code>
/// The folder name is the target file's stem, made unique with " (n)".
/// </summary>
public static class JobLayout
{
    private const int MaxAttempts = 10000;

    public static JobPaths Create(
        string folder,
        string? suggestedFileName,
        Func<string, bool>? isReserved = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new ArgumentException("A download folder is required.", nameof(folder));
        }

        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);

        var safeName = FileNameSanitizer.Sanitize(suggestedFileName);
        var extension = Path.GetExtension(safeName);
        var stem = Path.GetFileNameWithoutExtension(safeName);

        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "download";
        }

        for (var index = 0; index < MaxAttempts; index++)
        {
            var uniqueStem = index == 0 ? stem : $"{stem} ({index})";
            var fileName = uniqueStem + extension;
            var taskDirectory = Path.Combine(root, uniqueStem);
            var targetPath = Path.Combine(taskDirectory, fileName);

            if (File.Exists(taskDirectory) || Directory.Exists(taskDirectory))
            {
                continue;
            }

            if (isReserved?.Invoke(taskDirectory) == true || isReserved?.Invoke(targetPath) == true)
            {
                continue;
            }

            var processDirectory = Path.Combine(taskDirectory, StatePaths.ProcessDirectoryName);
            Directory.CreateDirectory(taskDirectory);

            return new JobPaths(
                taskDirectory,
                targetPath,
                processDirectory,
                StatePaths.StateIn(processDirectory, targetPath));
        }

        throw new IOException($"Could not find a free task folder for '{safeName}' in '{root}'.");
    }
}
