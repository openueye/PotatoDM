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
            catch (Exception ex) when (attempt < retry.MaxRetries && IsRetryable(ex, cancellationToken))
            {
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
        ProgressAggregator progress,
        CancellationToken cancellationToken)
    {
        var jobs = new List<FetchJob>();

        foreach (var track in tracks)
        {
            foreach (var initialization in track.Initializations)
            {
                if (!File.Exists(initialization.Path))
                {
                    jobs.Add(FetchJob.ForInitialization(initialization));
                }
            }

            foreach (var segment in track.Segments)
            {
                if (!File.Exists(segment.Path))
                {
                    jobs.Add(FetchJob.ForSegment(segment));
                }
            }
        }

        if (jobs.Count == 0)
        {
            return;
        }

        await Parallel.ForEachAsync(
                jobs,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Math.Min(16, jobs.Count)),
                    CancellationToken = cancellationToken,
                },
                async (job, token) =>
                {
                    using var lease = await limiter.AcquireAsync(job.Uri, token).ConfigureAwait(false);
                    progress.Enter();

                    try
                    {
                        await FetchPartWithRetryAsync(
                                client,
                                job,
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
        FetchJob job,
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
                        job,
                        requestOptions,
                        keys,
                        bandwidthLimiter,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < retry.MaxRetries && IsRetryable(ex, cancellationToken))
            {
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
        FetchJob job,
        RequestOptions requestOptions,
        Dictionary<string, byte[]> keys,
        IBandwidthLimiter? bandwidthLimiter,
        ProgressAggregator progress,
        CancellationToken cancellationToken)
    {
        var temporary = job.Path + ".tmp";

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

            progress.Add(read);
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
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _gate = new();
        private long _downloaded;
        private long _active;
        private long _lastReport;

        public ProgressAggregator(Action<DownloadProgress>? onProgress)
        {
            _onProgress = onProgress;
        }

        public void Enter() => Interlocked.Increment(ref _active);

        public void Leave() => Interlocked.Decrement(ref _active);

        public void Add(int bytes)
        {
            Interlocked.Add(ref _downloaded, bytes);

            var now = Stopwatch.GetTimestamp();
            var minimum = Stopwatch.Frequency / 4;
            var last = Interlocked.Read(ref _lastReport);

            if (now - last < minimum)
            {
                return;
            }

            Interlocked.Exchange(ref _lastReport, now);

            lock (_gate)
            {
                var total = Interlocked.Read(ref _downloaded);
                var seconds = Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds);
                _onProgress?.Invoke(new DownloadProgress(
                    total,
                    null,
                    total / seconds,
                    null,
                    (int)Interlocked.Read(ref _active)));
            }
        }
    }
}
