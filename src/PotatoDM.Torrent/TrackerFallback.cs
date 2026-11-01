using MonoTorrent.Client;

namespace PotatoDM.Torrent;

/// <summary>Adds the public fallback trackers used when a torrent has no tracker tiers.</summary>
internal static class TrackerFallback
{
    public static async Task AddAsync(TorrentManager manager)
    {
        var trackerManager = manager.TrackerManager;

        // Private torrents must only use the trackers their publisher chose.
        if (trackerManager.Private || trackerManager.Tiers.Count > 0)
        {
            return;
        }

        foreach (var text in TorrentMetadataFile.FallbackTrackers)
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                continue;
            }

            try
            {
                await trackerManager.AddTrackerAsync(uri).ConfigureAwait(false);
            }
            catch
            {
                // An unusable fallback tracker must not fail the download.
            }
        }
    }
}
