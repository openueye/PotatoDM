using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Http;
using PotatoDM.Core.Models;
using PotatoDM.Core.Transfer;
using static PotatoDM.Core.Media.MediaHttp;

namespace PotatoDM.Core.Media;

public static partial class MediaDownloader
{
    private const long MaxKeyBytes = 1024 * 1024;

    private static async Task FetchKeysAsync(
        HttpClient client,
        MediaSourcePlan plan,
        RequestOptions requestOptions,
        RetryPolicy retry,
        Dictionary<string, byte[]> keys,
        CancellationToken cancellationToken)
    {
        var keyUris = plan.Tracks
            .SelectMany(track => track.Segments)
            .Select(segment => segment.Key?.Uri)
            .Where(uri => uri is not null)
            .Select(uri => uri!)
            .Distinct();

        foreach (var keyUri in keyUris)
        {
            var key = await FetchKeyAsync(client, keyUri, requestOptions, retry, cancellationToken)
                .ConfigureAwait(false);
            keys[keyUri.AbsoluteUri] = key;
        }
    }

    private static async Task<byte[]> FetchKeyAsync(
        HttpClient client,
        Uri keyUri,
        RequestOptions requestOptions,
        RetryPolicy retry,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = DownloadRequestFactory.CreatePlainGet(keyUri, requestOptions);
                var (response, _) = await HttpSender.SendAsync(client, request, cancellationToken)
                    .ConfigureAwait(false);

                using (response)
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw CreateHttpException(response, keyUri);
                    }

                    EnsureIdentityEncoding(response);

                    if (response.Content.Headers.ContentLength is > MaxKeyBytes)
                    {
                        throw new MediaManifestException($"The HLS key at '{keyUri}' is unexpectedly large.");
                    }

                    var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (raw.Length > MaxKeyBytes)
                    {
                        throw new MediaManifestException($"The HLS key at '{keyUri}' is unexpectedly large.");
                    }

                    return NormalizeKey(raw, keyUri);
                }
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken))
            {
                if (attempt >= retry.MaxRetries)
                {
                    throw MediaHttp.RetryExhausted(ex, keyUri, attempt + 1);
                }

                await Task.Delay(retry.GetDelay(attempt, null), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static byte[] NormalizeKey(byte[] raw, Uri keyUri)
    {
        if (raw.Length == 16)
        {
            return raw;
        }

        if (raw.Length is 32 or 34)
        {
            var text = Encoding.ASCII.GetString(raw).Trim();
            if (text.Length == 32)
            {
                try
                {
                    return Convert.FromHexString(text);
                }
                catch (FormatException)
                {
                }
            }
        }

        throw new MediaManifestException(
            $"The AES-128 key at '{keyUri}' is not 16 bytes (got {raw.Length}).");
    }

    private static async Task FetchAllPartsAsync(
        HttpClient client,
        IReadOnlyList<CachedTrack> tracks,
        RequestOptions requestOptions,
        RetryPolicy retry,
        ConnectionLimiter limiter,
        Dictionary<string, byte[]> keys,
        IBandwidthLimiter? bandwidthLimiter,
        int connections,
        Action<DownloadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var parts = new List<MediaPart>();
        var pending = new List<MediaPart>();

        foreach (var track in tracks)
        {
            foreach (var initialization in track.Initializations)
            {
                var part = new MediaPart(
                    parts.Count,
                    FetchJob.ForInitialization(initialization),
                    initialization.Value.Range?.Length ?? 0,
                    isInitialization: true);

                parts.Add(part);

                if (File.Exists(initialization.Path))
                {
                    part.MarkCompleted(new FileInfo(initialization.Path).Length);
                }
                else
                {
                    pending.Add(part);
                }
            }

            foreach (var segment in track.Segments)
            {
                var part = new MediaPart(
                    parts.Count,
                    FetchJob.ForSegment(segment),
                    segment.Segment.Range?.Length ?? 0,
                    isInitialization: false);

                parts.Add(part);

                if (File.Exists(segment.Path))
                {
                    part.MarkCompleted(new FileInfo(segment.Path).Length);
                }
                else
                {
                    pending.Add(part);
                }
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        var progress = new ProgressAggregator(onProgress, parts);

        await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Math.Min(connections, pending.Count)),
                    CancellationToken = cancellationToken,
                },
                async (part, token) =>
                {
                    using var lease = await limiter.AcquireAsync(part.Job.Uri, token).ConfigureAwait(false);
                    progress.Enter();

                    try
                    {
                        await FetchPartWithRetryAsync(
                                client,
                                part,
                                requestOptions,
                                retry,
                                keys,
                                bandwidthLimiter,
                                progress,
                                token)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        progress.Leave();
                    }
                })
            .ConfigureAwait(false);
    }

    private static async Task FetchPartWithRetryAsync(
        HttpClient client,
        MediaPart part,
        RequestOptions requestOptions,
        RetryPolicy retry,
        Dictionary<string, byte[]> keys,
        IBandwidthLimiter? bandwidthLimiter,
        ProgressAggregator progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await FetchPartOnceAsync(
                        client,
                        part,
                        requestOptions,
                        keys,
                        bandwidthLimiter,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken))
            {
                if (attempt >= retry.MaxRetries)
                {
                    throw MediaHttp.RetryExhausted(ex, part.Job.Uri, attempt + 1);
                }

                var retryAfter = ex.Data.Contains("RetryAfter") &&
                                 ex.Data["RetryAfter"] is TimeSpan delay
                    ? delay
                    : (TimeSpan?)null;

                await Task.Delay(retry.GetDelay(attempt, retryAfter), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task FetchPartOnceAsync(
        HttpClient client,
        MediaPart part,
        RequestOptions requestOptions,
        Dictionary<string, byte[]> keys,
        IBandwidthLimiter? bandwidthLimiter,
        ProgressAggregator progress,
        CancellationToken cancellationToken)
    {
        var job = part.Job;
        var temporary = job.Path + ".tmp";
        progress.Reset(part);

        try
        {
            using var request = DownloadRequestFactory.CreatePlainGet(job.Uri, requestOptions);

            if (job.Range is { } range)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                    range.Offset,
                    range.EndInclusive);
            }

            var (response, _) = await HttpSender.SendAsync(client, request, cancellationToken)
                .ConfigureAwait(false);

            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                {
                    throw CreateHttpException(response, job.Uri);
                }

                EnsureIdentityEncoding(response);

                var expectedLength = job.Range?.Length ?? response.Content.Headers.ContentLength;

                if (expectedLength is > 0)
                {
                    progress.SetTotal(part, expectedLength.Value);
                }

                var body = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                await using var bodyScope = body.ConfigureAwait(false);

                var skip = job.Range is { } requested && response.StatusCode == HttpStatusCode.OK
                    ? requested.Offset
                    : 0;
                var limit = job.Range?.Length ?? long.MaxValue;

                using var bounded = new BoundedReadStream(body, skip, limit);

                var output = new FileStream(
                    temporary,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                await using (output.ConfigureAwait(false))
                {
                    var key = job.Key is { Uri: { } keyUri } ? keys[keyUri.AbsoluteUri] : null;

                    if (key is null)
                    {
                        await CopyWithProgressAsync(
                                bounded,
                                output,
                                bandwidthLimiter,
                                progress,
                                part,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        var iv = job.Key!.Iv ?? Aes128Cbc.DeriveIv(job.Sequence);

                        using var aes = Aes.Create();
                        aes.Key = key;
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;

                        var decrypting = new CryptoStream(
                            bounded,
                            aes.CreateDecryptor(),
                            CryptoStreamMode.Read,
                            leaveOpen: true);

                        await using var decryptingScope = decrypting.ConfigureAwait(false);

                        await CopyWithProgressAsync(
                                decrypting,
                                output,
                                bandwidthLimiter,
                                progress,
                                part,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                if (job.Range is { } expected && bounded.TotalRead != expected.Length)
                {
                    throw new HttpDownloadException(
                        $"Media segment '{job.Uri}' returned {bounded.TotalRead} bytes " +
                        $"but {expected.Length} were requested.",
                        isTransient: true);
                }
            }

            File.Move(temporary, job.Path, overwrite: true);
            progress.Complete(part);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        IBandwidthLimiter? bandwidthLimiter,
        ProgressAggregator progress,
        MediaPart part,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

            if (bandwidthLimiter is not null)
            {
                await bandwidthLimiter.ThrottleAsync(read, cancellationToken).ConfigureAwait(false);
            }

            progress.Add(part, read);
        }
    }

    private sealed class MediaPart
    {
        public MediaPart(int index, FetchJob job, long knownTotal, bool isInitialization)
        {
            Index = index;
            Job = job;
            Total = knownTotal;
            IsInitialization = isInitialization;
        }

        public int Index { get; }

        public bool IsInitialization { get; }

        public FetchJob Job { get; }

        public long Downloaded { get; set; }

        public long Total { get; set; }

        public long CompletedBytes { get; private set; }

        public bool Completed { get; private set; }

        public void MarkCompleted(long bytes)
        {
            Completed = true;
            CompletedBytes = bytes;
            Downloaded = bytes;

            if (bytes > 0)
            {
                Total = bytes;
            }
        }
    }

    private sealed class FetchJob
    {
        private FetchJob(Uri uri, ByteRange? range, MediaKey? key, long sequence, string path)
        {
            Uri = uri;
            Range = range;
            Key = key;
            Sequence = sequence;
            Path = path;
        }

        public Uri Uri { get; }

        public ByteRange? Range { get; }

        public MediaKey? Key { get; }

        public long Sequence { get; }

        public string Path { get; }

        public static FetchJob ForSegment(CachedSegment segment) => new(
            segment.Segment.Uri,
            segment.Segment.Range,
            segment.Segment.Key,
            segment.Segment.Sequence,
            segment.Path);

        public static FetchJob ForInitialization(CachedInitialization initialization) => new(
            initialization.Value.Uri,
            initialization.Value.Range,
            null,
            0,
            initialization.Path);
    }

    private sealed class ProgressAggregator
    {
        private readonly Action<DownloadProgress>? _onProgress;
        private readonly IReadOnlyList<MediaPart> _parts;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _gate = new();
        private long _lastReport;
        private int _active;

        public ProgressAggregator(
            Action<DownloadProgress>? onProgress,
            IReadOnlyList<MediaPart> parts)
        {
            _onProgress = onProgress;
            _parts = parts;
        }

        public void Enter() => Interlocked.Increment(ref _active);

        public void Leave() => Interlocked.Decrement(ref _active);

        public void Reset(MediaPart part)
        {
            lock (_gate)
            {
                part.Downloaded = 0;
            }
        }

        public void SetTotal(MediaPart part, long total)
        {
            lock (_gate)
            {
                if (total > 0)
                {
                    part.Total = total;
                }
            }
        }

        public void Add(MediaPart part, int bytes)
        {
            lock (_gate)
            {
                part.Downloaded += bytes;
            }

            MaybeReport(force: false);
        }

        public void Complete(MediaPart part)
        {
            bool allCompleted;

            lock (_gate)
            {
                part.MarkCompleted(new FileInfo(part.Job.Path).Length);
                allCompleted = _parts.All(item => item.Completed);
            }

            // One completion report per part would flood a thousand-segment
            // playlist; report at the normal cadence and force the final state.
            MaybeReport(allCompleted);
        }

        private void MaybeReport(bool force)
        {
            var now = Stopwatch.GetTimestamp();
            var minimum = Stopwatch.Frequency / 4;
            var last = Interlocked.Read(ref _lastReport);

            if (!force && now - last < minimum)
            {
                return;
            }

            Interlocked.Exchange(ref _lastReport, now);
            Report();
        }

        private void Report()
        {
            DownloadProgress progress;

            lock (_gate)
            {
                long completedBytes = 0;
                long inFlightBytes = 0;
                long knownTotal = 0;
                var completedParts = 0;
                var activeParts = 0;
                var everyPartKnown = _parts.Count > 0;

                foreach (var part in _parts)
                {
                    if (part.Completed)
                    {
                        completedParts++;
                        completedBytes += part.CompletedBytes;
                    }
                    else
                    {
                        inFlightBytes += part.Downloaded;

                        if (part.Downloaded > 0)
                        {
                            activeParts++;
                        }
                    }

                    if (part.Total > 0)
                    {
                        knownTotal += part.Total;
                    }
                    else
                    {
                        everyPartKnown = false;
                    }
                }

                long? estimate = null;

                if (everyPartKnown)
                {
                    estimate = knownTotal;
                }
                else if (completedParts > 0)
                {
                    var average = completedBytes / completedParts;
                    var remaining = _parts.Count - completedParts;
                    var unknown = Math.Max(0, remaining - activeParts);
                    estimate = completedBytes + inFlightBytes + unknown * average;
                }
                else if (_parts.Count == 1 && _parts[0].Total > 0)
                {
                    estimate = _parts[0].Total;
                }

                var downloaded = completedBytes + inFlightBytes;
                var seconds = Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds);
                var speed = downloaded / seconds;
                TimeSpan? eta = estimate is { } total && total > downloaded && speed > 0
                    ? TimeSpan.FromSeconds((total - downloaded) / speed)
                    : null;

                progress = new DownloadProgress(
                    downloaded,
                    estimate,
                    speed,
                    eta,
                    _active,
                    BuildSegments());
            }

            _onProgress?.Invoke(progress);
        }

        private DownloadSegmentProgress[]? BuildSegments()
        {
            var count = _parts.Count(part => !part.IsInitialization);

            if (count == 0)
            {
                return null;
            }

            var segments = new DownloadSegmentProgress[count];
            var index = 0;

            foreach (var part in _parts)
            {
                if (part.IsInitialization)
                {
                    continue;
                }

                var downloaded = part.Completed ? part.CompletedBytes : part.Downloaded;
                segments[index] = new DownloadSegmentProgress(index, downloaded, part.Total);
                index++;
            }

            return segments;
        }

        private static double Fraction(MediaPart part)
        {
            if (part.Completed)
            {
                return 1;
            }

            return part.Total > 0
                ? Math.Clamp((double)part.Downloaded / part.Total, 0, 1)
                : 0;
        }
    }
}
