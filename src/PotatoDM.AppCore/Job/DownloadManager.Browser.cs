using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Exceptions;
using PotatoDM.Torrent;

namespace PotatoDM.AppCore.Job;

public sealed partial class DownloadManager
{
    private const int BrowserResultLimit = 128;

    private static readonly TimeSpan BrowserResultLifetime = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, (BrowserAddResult Result, DateTimeOffset At)> _browserResults =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Accepts a hand-off from the browser extension. The returned result is
    /// the complete answer: the placeholder job is removed on rejection so no
    /// failed row is left behind.
    /// </summary>
    public async Task<BrowserAddResult> AddFromBrowserAsync(
        BrowserDownloadContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var isMagnet =
            string.Equals(context.Kind, "magnet", StringComparison.OrdinalIgnoreCase) ||
            context.Url.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase);

        if (isMagnet
                ? !context.Url.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)
                : context.Url.Scheme is not ("http" or "https"))
        {
            return BrowserAddResult.Reject(
                "invalid_request",
                isMagnet ? "磁力链接无效。" : "仅支持 http/https 下载。");
        }

        if (TryGetCachedBrowserResult(context.RequestId) is { } cached)
        {
            return cached;
        }

        var folder = SelectBrowserFolder();
        if (folder is null)
        {
            return CacheBrowserResult(
                context.RequestId,
                BrowserAddResult.Reject(
                    "storage_unavailable",
                    "保存目录不可用，已交给浏览器下载。"));
        }

        var metadata = new JobRequestMetadata
        {
            Referer = context.Referer,
            UserAgent = context.UserAgent,
            Cookies = context.Cookies,
            ExtraHeaders = context.ExtraHeaders,
        };

        if (isMagnet)
        {
            return await AddBrowserMagnetAsync(context, folder, metadata, cancellationToken);
        }

        var job = new DownloadJob(Guid.NewGuid().ToString("N"), context.Url.ToString(), DateTimeOffset.UtcNow)
        {
            RequestMetadata = metadata,
        };

        _jobs.Add(job);

        var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(_browserProbeTimeout);

        lock (_sync)
        {
            _cancellations[job.Id] = probeCts;
        }

        try
        {
            var engine = _engines.Create();
            var probe = await engine.ProbeAsync(
                context.Url,
                CreateRequestOptions(metadata),
                probeCts.Token);

            if (IsRemoved(job.Id))
            {
                return BrowserAddResult.Reject("cancelled", "下载已被取消。");
            }

            var isMedia = IsMediaRequest(context.Url, probe);

            if (probe.TotalLength is null && !isMedia)
            {
                _jobs.Remove(job);
                return CacheBrowserResult(
                    context.RequestId,
                    BrowserAddResult.Reject(
                        "no_length",
                        "服务器未提供文件长度，已交给浏览器下载。"));
            }

            var fileName = ChooseFileName(probe.SuggestedFileName, context.SuggestedFileName);
            if (isMedia)
            {
                fileName = ProvisionalMediaFileName(fileName, probe.FinalUri);
            }

            var layout = ReserveLayout(folder, fileName);
            RegisterJob(
                job,
                layout,
                probe.TotalLength ?? 0,
                isMedia,
                isTorrent: false,
                metadata);

            if (IsRemoved(job.Id))
            {
                return BrowserAddResult.Reject("cancelled", "下载已被取消。");
            }

            var connections = _settings.AutomaticConnections && !isMedia && probe.TotalLength is not null
                ? DownloadConnectionPolicy.Select(probe.TotalLength.Value, probe.SupportsRanges)
                : _settings.Connections;
            Enqueue(job, connections, isResume: false);

            return CacheBrowserResult(context.RequestId, BrowserAddResult.Accept(job));
        }
        catch (OperationCanceledException) when (probeCts.IsCancellationRequested)
        {
            CleanupAfterFailure(job);
            _jobs.Remove(job);

            var cancelled = cancellationToken.IsCancellationRequested || IsRemoved(job.Id);

            if (!cancelled)
            {
                _log.Debug(
                    $"Browser probe of '{context.Url}' timed out after " +
                    $"{_browserProbeTimeout.TotalSeconds:0.#}s; handing off to the browser.");
            }

            return CacheBrowserResult(
                context.RequestId,
                cancelled
                    ? BrowserAddResult.Reject("cancelled", "下载已被取消。")
                    : BrowserAddResult.Reject(
                        "probe_timeout",
                        "探测下载地址超时，已交给浏览器下载。"));
        }
        catch (Exception ex)
        {
            _log.Debug($"Browser hand-off for '{context.Url}' fell back to the browser.", ex);
            CleanupAfterFailure(job);
            _jobs.Remove(job);
            return CacheBrowserResult(context.RequestId, MapBrowserFailure(ex));
        }
        finally
        {
            RemoveProbeCancellation(job.Id, probeCts);
            probeCts.Dispose();
        }
    }

    /// <summary>
    /// Queues a magnet hand-off straight away: there is nothing to probe, and
    /// a malformed link is rejected before any row appears in the list.
    /// </summary>
    private Task<BrowserAddResult> AddBrowserMagnetAsync(
        BrowserDownloadContext context,
        string folder,
        JobRequestMetadata metadata,
        CancellationToken cancellationToken)
    {
        DownloadJob? job = null;

        try
        {
            var source = context.Url.OriginalString;
            TorrentSource.Validate(source);

            var layout = ReserveLayout(folder, TorrentSource.SuggestedName(source));
            job = new DownloadJob(Guid.NewGuid().ToString("N"), source, DateTimeOffset.UtcNow)
            {
                RequestMetadata = metadata,
            };
            job.SetDisplayName(TorrentSource.SuggestedName(source));

            _jobs.Add(job);
            RegisterJob(
                job,
                layout,
                0,
                isMedia: false,
                isTorrent: true,
                metadata);

            if (IsRemoved(job.Id))
            {
                return Task.FromResult(CacheBrowserResult(
                    context.RequestId,
                    BrowserAddResult.Reject("cancelled", "下载已被取消。")));
            }

            Enqueue(job, _settings.Connections, isResume: false);
            return Task.FromResult(
                CacheBrowserResult(context.RequestId, BrowserAddResult.Accept(job)));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(CacheBrowserResult(
                context.RequestId,
                BrowserAddResult.Reject("cancelled", "下载已被取消。")));
        }
        catch (Exception ex)
        {
            if (job is not null)
            {
                CleanupAfterFailure(job);

                lock (_sync)
                {
                    _records.RemoveAll(record => record.Id == job.Id);
                }

                _jobs.Remove(job);
            }

            return Task.FromResult(
                CacheBrowserResult(context.RequestId, MapBrowserFailure(ex)));
        }
    }

    private string? SelectBrowserFolder()
    {
        var preferred = _settings.DownloadFolder;

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            try
            {
                var full = Path.GetFullPath(preferred);
                if (Directory.Exists(full) && IsDirectoryWritable(full))
                {
                    return full;
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _log.Debug($"Configured download folder '{preferred}' is not usable.", ex);
            }
        }

        try
        {
            var fallback = Path.GetFullPath(_defaultFolder());
            Directory.CreateDirectory(fallback);
            return IsDirectoryWritable(fallback) ? fallback : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            _log.Debug("The fallback download folder is not usable.", ex);
            return null;
        }
    }

    private bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".potatodm-write-" + Guid.NewGuid().ToString("N") + ".tmp");

            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            return true;
        }
        catch (Exception ex)
        {
            _log.Debug($"Probe write to '{directory}' failed; the folder is treated as read-only.", ex);
            return false;
        }
    }

    private static string? ChooseFileName(string? probeName, string? browserName)
    {
        if (!string.IsNullOrWhiteSpace(probeName) &&
            !probeName.Equals("download.bin", StringComparison.OrdinalIgnoreCase))
        {
            return probeName;
        }

        return string.IsNullOrWhiteSpace(browserName) ? probeName : browserName;
    }

    private static BrowserAddResult MapBrowserFailure(Exception exception) => exception switch
    {
        DrmProtectedException => BrowserAddResult.Reject(
            "drm_protected",
            "该媒体受 DRM 保护；土豆下载器不会绕过内容保护。"),
        MediaManifestException => BrowserAddResult.Reject(
            "media_unsupported",
            "无法解析或合并不受支持的媒体清单。"),
        MediaMergeException => BrowserAddResult.Reject(
            "media_merge_unavailable",
            "合并分离的音视频轨需要 ffmpeg，请先安装或在设置中指定路径。"),
        TorrentException => BrowserAddResult.Reject(
            "unsupported_torrent",
            "磁力链接无效或暂不受支持。"),
        UnsupportedContentEncodingException => BrowserAddResult.Reject(
            "unsupported_encoding",
            "服务器返回了不支持的压缩格式，已交给浏览器下载。"),
        HttpDownloadException { IsTransient: true } => BrowserAddResult.Reject(
            "probe_failed",
            "探测下载地址失败，已交给浏览器下载。"),
        HttpDownloadException => BrowserAddResult.Reject(
            "http_error",
            "服务器拒绝了下载请求，已交给浏览器下载。"),
        ProbeException => BrowserAddResult.Reject(
            "probe_failed",
            "探测下载地址失败，已交给浏览器下载。"),
        FileAccessException or DiskSpaceException or UnauthorizedAccessException =>
            BrowserAddResult.Reject(
                "storage_unavailable",
                "保存目录不可用，已交给浏览器下载。"),
        IOException => BrowserAddResult.Reject(
            "probe_failed",
            "网络请求失败，已交给浏览器下载。"),
        _ => BrowserAddResult.Reject(
            "internal_error",
            "客户端处理下载请求失败，已交给浏览器下载。"),
    };

    private BrowserAddResult? TryGetCachedBrowserResult(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        lock (_sync)
        {
            if (!_browserResults.TryGetValue(requestId, out var entry))
            {
                return null;
            }

            if (DateTimeOffset.UtcNow - entry.At <= BrowserResultLifetime)
            {
                return entry.Result;
            }

            _browserResults.Remove(requestId);
            return null;
        }
    }

    private BrowserAddResult CacheBrowserResult(string? requestId, BrowserAddResult result)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return result;
        }

        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var expired in _browserResults
                         .Where(pair => now - pair.Value.At > BrowserResultLifetime)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _browserResults.Remove(expired);
            }

            if (_browserResults.Count >= BrowserResultLimit)
            {
                _browserResults.Clear();
            }

            _browserResults[requestId] = (result, now);
        }

        return result;
    }
}
