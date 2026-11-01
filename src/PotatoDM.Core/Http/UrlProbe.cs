using System.Net;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Models;
using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Http;

/// <summary>
/// Determines length, range support, validators and a suggested file name.
/// The primary request is a GET with Range 0-0 because many CDNs reject HEAD.
/// </summary>
public sealed class UrlProbe
{
    private readonly RetryPolicy _retry;

    public UrlProbe(RetryPolicy? retry = null)
    {
        _retry = retry ?? new RetryPolicy(3, TimeSpan.FromSeconds(10));
    }

    public async Task<ProbeResult> ProbeAsync(
        HttpClient client,
        Uri url,
        RequestOptions options,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt <= _retry.MaxRetries; attempt++)
        {
            try
            {
                return await ProbeOnceAsync(client, url, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _retry.MaxRetries && IsRetryableProbeFailure(ex))
            {
                lastError = ex;
                await Task.Delay(_retry.GetDelay(attempt, null), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new ProbeException($"Failed to probe '{url}'.", lastError);
    }

    private static async Task<ProbeResult> ProbeOnceAsync(
        HttpClient client,
        Uri url,
        RequestOptions options,
        CancellationToken cancellationToken)
    {
        using var request = DownloadRequestFactory.CreateProbe(url, options);
        var (response, finalUri) = await HttpSender.SendAsync(client, request, cancellationToken)
            .ConfigureAwait(false);

        using (response)
        {
            EnsureIdentityEncoding(response);

            switch (response.StatusCode)
            {
                case HttpStatusCode.PartialContent:
                    return FromPartialContent(url, finalUri, response);

                case HttpStatusCode.OK:
                    return Build(url, finalUri, response.Content.Headers.ContentLength,
                        supportsRanges: false, response);

                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    return FromUnsatisfiable(url, finalUri, response);

                case HttpStatusCode.MethodNotAllowed:
                case HttpStatusCode.NotImplemented:
                    break;

                default:
                    ThrowForStatus(response);
                    break;
            }
        }

        return await ProbeWithHeadAsync(client, url, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProbeResult> ProbeWithHeadAsync(
        HttpClient client,
        Uri url,
        RequestOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var head = DownloadRequestFactory.CreateHead(url, options);
            var (response, finalUri) = await HttpSender.SendAsync(client, head, cancellationToken)
                .ConfigureAwait(false);

            using (response)
            {
                EnsureIdentityEncoding(response);

                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
                {
                    return Build(url, finalUri, response.Content.Headers.ContentLength,
                        HasAcceptRanges(response), response);
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    return FromUnsatisfiable(url, finalUri, response);
                }

                ThrowForStatus(response);
            }
        }
        catch (HttpDownloadException ex) when (!ex.IsTransient)
        {
            // HEAD may be forbidden even when GET works; try a body-less GET below.
        }
        catch (HttpRequestException)
        {
            // Fall through to plain GET.
        }

        return await ProbeWithPlainGetAsync(client, url, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProbeResult> ProbeWithPlainGetAsync(
        HttpClient client,
        Uri url,
        RequestOptions options,
        CancellationToken cancellationToken)
    {
        using var request = DownloadRequestFactory.CreatePlainGet(url, options);
        var (response, finalUri) = await HttpSender.SendAsync(client, request, cancellationToken)
            .ConfigureAwait(false);

        using (response)
        {
            EnsureIdentityEncoding(response);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
            {
                return Build(url, finalUri, response.Content.Headers.ContentLength,
                    HasAcceptRanges(response), response);
            }

            ThrowForStatus(response);
            throw new ProbeException($"Unexpected status {(int)response.StatusCode} while probing.");
        }
    }

    private static ProbeResult FromPartialContent(Uri requested, Uri finalUri, HttpResponseMessage response)
    {
        var contentRangeHeader = response.Content.Headers.ContentRange?.ToString();
        if (!ContentRangeParser.TryParse(contentRangeHeader, out var range) || range.TotalLength is null)
        {
            var fallbackLength = response.Content.Headers.ContentLength;
            if (fallbackLength is null)
            {
                throw new ProbeException("The server returned 206 without a usable Content-Range header.");
            }

            return Build(requested, finalUri, fallbackLength, supportsRanges: true, response);
        }

        return Build(requested, finalUri, range.TotalLength, supportsRanges: true, response);
    }

    private static ProbeResult FromUnsatisfiable(Uri requested, Uri finalUri, HttpResponseMessage response)
    {
        var header = response.Content.Headers.ContentRange?.ToString();
        if (ContentRangeParser.TryParse(header, out var range) && range.TotalLength is { } total)
        {
            return Build(requested, finalUri, total, supportsRanges: true, response);
        }

        throw new ProbeException("The server returned 416 without a usable Content-Range header.");
    }

    private static ProbeResult Build(
        Uri requested,
        Uri finalUri,
        long? totalLength,
        bool supportsRanges,
        HttpResponseMessage response)
    {
        var etag = response.Headers.ETag?.ToString();
        var lastModified = response.Content.Headers.LastModified;

        string? rawDisposition = null;
        if (response.Content.Headers.TryGetValues("Content-Disposition", out var values))
        {
            rawDisposition = values.FirstOrDefault();
        }

        var suggested = ContentDispositionParser.GetFileName(rawDisposition);

        if (string.IsNullOrWhiteSpace(suggested))
        {
            var leaf = finalUri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrEmpty(leaf))
            {
                try
                {
                    suggested = Uri.UnescapeDataString(leaf);
                }
                catch (UriFormatException)
                {
                    suggested = leaf;
                }
            }
        }

        var safeName = FileNameSanitizer.Sanitize(suggested);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        return new ProbeResult(
            requested,
            finalUri,
            totalLength,
            supportsRanges,
            etag,
            lastModified,
            safeName,
            contentType);
    }

    private static bool HasAcceptRanges(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Accept-Ranges", out var values))
        {
            return false;
        }

        return values.Any(v => v.Contains("bytes", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureIdentityEncoding(HttpResponseMessage response)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count > 0 &&
            encodings.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnsupportedContentEncodingException(string.Join(", ", encodings));
        }
    }

    private static void ThrowForStatus(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var message = $"Probe request failed with HTTP {status} ({response.StatusCode}).";
        if (RetryPolicy.IsRetryableStatus(response.StatusCode))
        {
            var retryAfter = RetryAfterParser.Parse(response);
            throw new HttpDownloadException(message, isTransient: true) { Data = { ["RetryAfter"] = retryAfter } };
        }

        throw new HttpDownloadException(message, isTransient: false);
    }

    private static bool IsRetryableProbeFailure(Exception exception) => exception switch
    {
        HttpDownloadException http => http.IsTransient,
        HttpRequestException => true,
        IOException => true,
        _ => false,
    };
}
