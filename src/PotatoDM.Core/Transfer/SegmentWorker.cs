using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Http;
using PotatoDM.Core.Internal;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;

namespace PotatoDM.Core.Transfer;

internal static class SegmentWorker
{
    public static async Task RunAsync(
        JobRuntime runtime,
        DownloadSegment segment,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunCoreAsync(runtime, segment, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            runtime.RecordFatal(ex);
            throw;
        }
    }

    private static async Task RunCoreAsync(
        JobRuntime runtime,
        DownloadSegment segment,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (segment.Remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesBefore = segment.BytesDone;
            var lease = await runtime.Limiter
                .AcquireAsync(runtime.FinalUri, cancellationToken)
                .ConfigureAwait(false);

            var retryable = false;
            TimeSpan? retryAfter = null;
            Exception? lastError = null;

            Interlocked.Increment(ref runtime.ActiveConnections);
            try
            {
                try
                {
                    await AttemptOnceAsync(runtime, segment, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (RangeIgnoredException)
                {
                    throw;
                }
                catch (ValidatorMismatchException)
                {
                    throw;
                }
                catch (UnsupportedContentEncodingException)
                {
                    throw;
                }
                catch (HttpDownloadException ex) when (!ex.IsTransient)
                {
                    throw;
                }
                catch (RetryableHttpException ex)
                {
                    retryable = true;
                    retryAfter = ex.RetryAfter;
                    lastError = ex;
                }
                catch (Exception ex) when (IsRetryable(ex))
                {
                    retryable = true;
                    lastError = ex;
                }
            }
            finally
            {
                Interlocked.Decrement(ref runtime.ActiveConnections);
                lease.Dispose();
            }

            if (!retryable)
            {
                return;
            }

            var progressed = segment.BytesDone > bytesBefore;
            attempt = progressed ? 0 : attempt + 1;

            if (attempt >= runtime.Retry.MaxRetries)
            {
                throw new HttpDownloadException(
                    $"Segment {segment.Index} failed after {attempt} attempts.",
                    IsTransientRetryExhaustion(lastError),
                    lastError);
            }

            var delay = runtime.Retry.GetDelay(attempt, retryAfter);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task AttemptOnceAsync(
        JobRuntime runtime,
        DownloadSegment segment,
        CancellationToken cancellationToken)
    {
        var startOffset = segment.NextOffset;

        if (!runtime.Probe.SupportsRanges && startOffset != 0)
        {
            throw new StateException(
                "The server does not support Range requests, so this job cannot resume mid-file.");
        }

        var usedRange = runtime.Probe.SupportsRanges;
        using var request = usedRange
            ? DownloadRequestFactory.CreateSegment(
                runtime.FinalUri,
                runtime.RequestOptions,
                startOffset,
                segment.EndExclusive - 1,
                runtime.Probe)
            : DownloadRequestFactory.CreatePlainGet(runtime.FinalUri, runtime.RequestOptions);

        var (response, _) = await HttpSender
            .SendAsync(runtime.Client, request, cancellationToken)
            .ConfigureAwait(false);

        using (response)
        {
            EnsureIdentityEncoding(response);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    if (usedRange)
                    {
                        // The server ignored Range. The engine must restart in single-stream mode.
                        throw new RangeIgnoredException();
                    }

                    var plainStream = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);

                    await using (plainStream.ConfigureAwait(false))
                    {
                        await CopyResponseToFileAsync(
                                plainStream,
                                runtime.File,
                                segment,
                                startOffset,
                                runtime.StallTimeout,
                                runtime.BandwidthLimiter,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (!segment.IsComplete)
                    {
                        throw new UnexpectedRangeResponseException(
                            "The connection ended before the resource was complete.",
                            isTransient: true);
                    }

                    return;

                case HttpStatusCode.PartialContent:
                {
                    ValidatePartialResponse(runtime, response, startOffset);

                    var stream = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);

                    await using var streamScope = stream.ConfigureAwait(false);

                    await CopyResponseToFileAsync(
                            stream,
                            runtime.File,
                            segment,
                            startOffset,
                            runtime.StallTimeout,
                            runtime.BandwidthLimiter,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (!segment.IsComplete)
                    {
                        throw new UnexpectedRangeResponseException(
                            "The connection ended before the segment was complete.",
                            isTransient: true);
                    }

                    return;
                }

                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    if (segment.IsComplete)
                    {
                        return;
                    }

                    throw new UnexpectedRangeResponseException(
                        "Server returned 416 while bytes remain in the segment.",
                        isTransient: false);

                default:
                    if (RetryPolicy.IsRetryableStatus(response.StatusCode))
                    {
                        throw new RetryableHttpException(
                            response.StatusCode,
                            RetryAfterParser.Parse(response));
                    }

                    throw new HttpDownloadException(
                        $"Segment {segment.Index} failed with HTTP {(int)response.StatusCode} " +
                        $"({response.StatusCode}).",
                        isTransient: false);
            }
        }
    }

    private static void ValidatePartialResponse(
        JobRuntime runtime,
        HttpResponseMessage response,
        long expectedStart)
    {
        var header = response.Content.Headers.ContentRange?.ToString();
        if (!ContentRangeParser.TryParse(header, out var range) || range.Start != expectedStart)
        {
            throw new UnexpectedRangeResponseException(
                $"Expected a Content-Range starting at {expectedStart}, got '{header ?? "<none>"}'.",
                isTransient: false);
        }

        if (range.TotalLength is { } total && total != runtime.State.TotalLength)
        {
            throw new ValidatorMismatchException(
                $"Remote length changed from {runtime.State.TotalLength} to {total}.");
        }

        var etag = response.Headers.ETag?.ToString();
        if (runtime.Probe.HasStrongETag &&
            !string.IsNullOrEmpty(etag) &&
            !string.Equals(etag, runtime.Probe.ETag, StringComparison.Ordinal))
        {
            throw new ValidatorMismatchException("Remote ETag changed while downloading.");
        }

        var lastModified = response.Content.Headers.LastModified;
        if (runtime.Probe.LastModified is { } expected &&
            lastModified is { } actual &&
            actual != expected)
        {
            throw new ValidatorMismatchException("Remote Last-Modified changed while downloading.");
        }
    }

    private static async Task CopyResponseToFileAsync(
        Stream stream,
        RandomAccessOutputFile file,
        DownloadSegment segment,
        long absoluteOffset,
        TimeSpan stallTimeout,
        IBandwidthLimiter? bandwidthLimiter,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var offset = absoluteOffset;

            while (offset < segment.EndExclusive)
            {
                var maxRead = (int)Math.Min(buffer.Length, segment.EndExclusive - offset);
                stallCts.CancelAfter(stallTimeout);

                int read;
                try
                {
                    read = await stream
                        .ReadAsync(buffer.AsMemory(0, maxRead), stallCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new StallTimeoutException(stallTimeout);
                }

                if (read <= 0)
                {
                    return;
                }

                // The range may have been split while this read was in flight.
                // Persist only up to the current boundary; a helper segment
                // re-fetches anything beyond it.
                var limit = segment.EndExclusive;
                var toWrite = (int)Math.Clamp(limit - offset, 0, read);
                if (toWrite == 0)
                {
                    return;
                }

                await file
                    .WriteAsync(buffer.AsMemory(0, toWrite), offset, cancellationToken)
                    .ConfigureAwait(false);

                segment.CommitBytes(toWrite);
                offset += toWrite;

                // Pace after the write so a cancelled wait never loses the
                // bytes already persisted or their progress accounting.
                if (bandwidthLimiter is not null)
                {
                    await bandwidthLimiter
                        .ThrottleAsync(toWrite, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (toWrite < read)
                {
                    // The rest of this read belongs to the helper now.
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsRetryable(Exception exception) => exception is
        StallTimeoutException or
        UnexpectedRangeResponseException or
        RetryableHttpException or
        HttpRequestException or
        IOException or
        TaskCanceledException;

    /// <summary>
    /// Classifies a failure that survived every retry.
    /// </summary>
    /// <remarks>
    /// Defaults to transient. A false "try again later" costs the caller a few extra
    /// attempts; a false "give up" makes a script abandon a download that would have
    /// succeeded minutes later. Only a deterministic protocol contradiction earns
    /// the permanent code, and each throw site states which kind it is.
    /// </remarks>
    internal static bool IsTransientRetryExhaustion(Exception? error) =>
        error is not UnexpectedRangeResponseException { IsTransient: false };

    private static void EnsureIdentityEncoding(HttpResponseMessage response)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count > 0 &&
            encodings.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnsupportedContentEncodingException(string.Join(", ", encodings));
        }
    }
}
