using MonoTorrent;
using MonoTorrent.BEncoding;

namespace PotatoDM.Torrent;

/// <summary>
/// Reads and adjusts .torrent bytes. The info hash is a torrent's identity, so
/// metadata from a magnet, a peer or an HTTP cache is always verified before it
/// is handed to libtorrent.
/// </summary>
internal static class TorrentMetadataFile
{
    /// <summary>
    /// Public trackers added to every public torrent. Link-site magnets often
    /// carry only dead trackers, and DHT alone is slow on networks where UDP is
    /// restricted; extra tiers give libtorrent more places to look for peers.
    /// </summary>
    internal static readonly string[] FallbackTrackers =
    {
        "http://tracker.opentrackr.org:1337/announce",
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://explodie.org:6969/announce",
        "udp://tracker.moeking.me:6969/announce",
        "https://tracker.tamersunion.org:443/announce",
        "udp://tracker.dler.org:6969/announce",
    };

    public static bool TryRead(string path, InfoHashes expected, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            return TryValidate(File.ReadAllBytes(path), expected, out bytes);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryValidate(byte[] candidate, InfoHashes expected, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (candidate.Length == 0)
        {
            return false;
        }

        try
        {
            var torrent = MonoTorrent.Torrent.Load(candidate);

            if (!expected.Contains(torrent.InfoHashes.V1OrV2))
            {
                return false;
            }

            bytes = candidate;
            return true;
        }
        catch
        {
            // Truncated, malformed or poisoned metadata is simply not usable.
            return false;
        }
    }

    /// <summary>
    /// Adds public tracker tiers to a public torrent, keeping the trackers it
    /// already carries. Private torrents must only use their publisher's
    /// trackers and are never touched. The info hash is verified before the
    /// rewritten metadata is returned.
    /// </summary>
    public static byte[] WithFallbackTrackers(byte[] bytes, bool enabled)
    {
        if (!enabled)
        {
            return bytes;
        }

        try
        {
            var dictionary = BEncodedValue.Decode<BEncodedDictionary>(bytes);
            var torrent = MonoTorrent.Torrent.Load(bytes);

            if (torrent.IsPrivate)
            {
                return bytes;
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tiers = new BEncodedList();

            if (dictionary.TryGetValue("announce-list", out var existingValue) &&
                existingValue is BEncodedList existingTiers)
            {
                foreach (var tier in existingTiers)
                {
                    if (tier is not BEncodedList tierList)
                    {
                        continue;
                    }

                    var copy = new BEncodedList();

                    foreach (var value in tierList)
                    {
                        if (value is BEncodedString url && !string.IsNullOrWhiteSpace(url.Text))
                        {
                            copy.Add(new BEncodedString(url.Text));
                            known.Add(url.Text);
                        }
                    }

                    if (copy.Count > 0)
                    {
                        tiers.Add(copy);
                    }
                }
            }
            else if (dictionary.TryGetValue("announce", out var announceValue) &&
                     announceValue is BEncodedString announce &&
                     !string.IsNullOrWhiteSpace(announce.Text))
            {
                tiers.Add(new BEncodedList { new BEncodedString(announce.Text) });
                known.Add(announce.Text);
            }

            // One URL per tier: BEP 12 tries trackers inside a tier in order,
            // so a slow first tracker would hide the rest of the list.
            foreach (var url in FallbackTrackers)
            {
                if (!known.Add(url))
                {
                    continue;
                }

                tiers.Add(new BEncodedList { new BEncodedString(url) });
            }

            dictionary["announce-list"] = tiers;

            var encoded = dictionary.Encode();
            var rewritten = MonoTorrent.Torrent.Load(encoded);

            // Re-encoding must never change the torrent's identity.
            return rewritten.InfoHashes.V1OrV2.Equals(torrent.InfoHashes.V1OrV2)
                ? encoded
                : bytes;
        }
        catch
        {
            // Adding trackers is an optimisation; it must never fail a download.
            return bytes;
        }
    }

    public static async Task WriteCacheAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        string? temporary = null;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation; the current transfer still has the bytes.
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                }
            }
        }
    }
}
