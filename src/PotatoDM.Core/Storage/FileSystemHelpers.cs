namespace PotatoDM.Core.Storage;

/// <summary>Best-effort filesystem operations shared by the app and the engine.</summary>
public static class FileSystemHelpers
{
    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
