namespace PotatoDM.AppCore.Services;

/// <summary>Selects a conservative connection count from the probed resource.</summary>
public static class DownloadConnectionPolicy
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    public static int Select(long totalLength, bool supportsRanges)
    {
        if (!supportsRanges || totalLength <= 2 * MiB)
        {
            return 1;
        }

        if (totalLength < 32 * MiB)
        {
            return 4;
        }

        return totalLength < GiB ? 8 : 16;
    }
}
