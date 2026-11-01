using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Http;

namespace PotatoDM.Core.Storage;

public static class StatePaths
{
    public const string StateSuffix = ".download.json";
    public const string WorkingSuffix = ".potatodm-part";

    /// <summary>
    /// Folder, beside the target file, that holds every process artifact
    /// (sidecars, working file and media/torrent caches).
    /// </summary>
    public const string ProcessDirectoryName = ".potatodm";

    /// <summary>HLS/DASH segment cache folder inside the process directory.</summary>
    public const string MediaCacheDirectoryName = "media";

    /// <summary>Torrent metadata cache folder inside the process directory.</summary>
    public const string TorrentCacheDirectoryName = "torrent-cache";

    public static string StateFor(string targetFile) => targetFile + StateSuffix;

    public static string WorkingFor(string targetFile) => targetFile + WorkingSuffix;

    public static string ProcessDirectoryFor(string targetFile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetFile));
        return string.IsNullOrEmpty(directory)
            ? Path.GetFullPath(ProcessDirectoryName)
            : Path.Combine(directory, ProcessDirectoryName);
    }

    /// <summary>Sidecar path inside an explicit process directory.</summary>
    public static string StateIn(string processDirectory, string targetFile) =>
        Path.Combine(processDirectory, Path.GetFileName(targetFile) + StateSuffix);

    /// <summary>Working-file path inside an explicit process directory.</summary>
    public static string WorkingIn(string processDirectory, string targetFile) =>
        Path.Combine(processDirectory, Path.GetFileName(targetFile) + WorkingSuffix);

    /// <summary>
    /// Returns the task folder for a job using the per-task layout, or null when
    /// the paths belong to the legacy flat layout (sidecars beside the target).
    /// </summary>
    public static string? TaskDirectoryFor(string targetFile, string statePath)
    {
        if (string.IsNullOrWhiteSpace(targetFile) || string.IsNullOrWhiteSpace(statePath))
        {
            return null;
        }

        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetFile));
        if (string.IsNullOrEmpty(targetDirectory))
        {
            return null;
        }

        var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(statePath));
        var expected = Path.Combine(targetDirectory, ProcessDirectoryName);

        return string.Equals(stateDirectory, expected, StringComparison.OrdinalIgnoreCase)
            ? targetDirectory
            : null;
    }

    /// <summary>Accepts either a state file or a working file and returns the state path.</summary>
    public static string ResolveStatePath(string stateOrWorkingPath)
    {
        if (string.IsNullOrWhiteSpace(stateOrWorkingPath))
        {
            throw new StateException("A state file path is required.");
        }

        var fullPath = Path.GetFullPath(stateOrWorkingPath);

        if (fullPath.EndsWith(StateSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        if (fullPath.EndsWith(WorkingSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath[..^WorkingSuffix.Length] + StateSuffix;
        }

        return fullPath;
    }
}

/// <summary>Resolves the final target path, creating directories and avoiding collisions.</summary>
public static class TargetPathResolver
{
    public static string Resolve(string? targetPath, string? suggestedFileName) =>
        Resolve(targetPath, suggestedFileName, avoidExistingArtifacts: false, isReserved: null);

    /// <summary>
    /// Resolves a target path. When <paramref name="avoidExistingArtifacts"/> is set,
    /// leftover sidecars or working files also count as occupied, so a new job can
    /// never adopt another job's state.
    /// </summary>
    public static string Resolve(
        string? targetPath,
        string? suggestedFileName,
        bool avoidExistingArtifacts,
        Func<string, bool>? isReserved = null)
    {
        var safeName = FileNameSanitizer.Sanitize(suggestedFileName);

        string fullPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            fullPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, safeName));
        }
        else
        {
            var expanded = Path.GetFullPath(targetPath);
            var looksLikeDirectory =
                Directory.Exists(expanded) ||
                targetPath.EndsWith(Path.DirectorySeparatorChar) ||
                targetPath.EndsWith(Path.AltDirectorySeparatorChar);

            fullPath = looksLikeDirectory ? Path.Combine(expanded, safeName) : expanded;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return IsTaken(fullPath, avoidExistingArtifacts, isReserved)
            ? MakeUnique(fullPath, avoidExistingArtifacts, isReserved)
            : fullPath;
    }

    public static string MakeUnique(string path) =>
        MakeUnique(path, avoidExistingArtifacts: false, isReserved: null);

    public static string MakeUnique(
        string path,
        bool avoidExistingArtifacts,
        Func<string, bool>? isReserved = null)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new FileAccessException($"Invalid target path '{path}'.");
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!IsTaken(candidate, avoidExistingArtifacts, isReserved))
            {
                return candidate;
            }
        }

        throw new FileAccessException($"Could not find a free file name for '{path}'.");
    }

    private static bool IsTaken(
        string path,
        bool avoidExistingArtifacts,
        Func<string, bool>? isReserved)
    {
        if (File.Exists(path) || isReserved?.Invoke(path) == true)
        {
            return true;
        }

        if (!avoidExistingArtifacts)
        {
            return false;
        }

        return File.Exists(path + StatePaths.StateSuffix) ||
               File.Exists(path + StatePaths.StateSuffix + ".bak") ||
               File.Exists(path + StatePaths.StateSuffix + ".tmp") ||
               File.Exists(path + StatePaths.StateSuffix + ".lock") ||
               File.Exists(path + StatePaths.WorkingSuffix);
    }
}
