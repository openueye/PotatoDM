using PotatoDM.Core.Diagnostics;

namespace PotatoDM.Core.Storage;

/// <summary>Best-effort filesystem operations shared by the app and the engine.</summary>
public static class FileSystemHelpers
{
    /// <summary>
    /// Deletes <paramref name="path"/> if it exists, tolerating an open handle.
    /// </summary>
    /// <param name="path">File to delete.</param>
    /// <param name="log">
    /// Optional sink. A delete that fails is how a cancelled job silently keeps
    /// gigabytes of orphaned scratch data, so it is logged at Warning rather than
    /// discarded. The method still never throws: cleanup runs from failure paths
    /// where a second exception would mask the first.
    /// </param>
    public static void TryDeleteFile(string path, ILogSink? log = null)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            log.Warning($"Leftover file '{path}' could not be deleted.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Warning($"Leftover file '{path}' could not be deleted.", ex);
        }
    }
}
