using System.Net.Http.Headers;
using PotatoDM.Core.Models;

namespace PotatoDM.Core.Http;

public static class DownloadRequestFactory
{
    /// <summary>Probe with the smallest possible Range request; HEAD is only a fallback.</summary>
    public static HttpRequestMessage CreateProbe(Uri uri, RequestOptions options)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyCommon(request, options);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        return request;
    }

    public static HttpRequestMessage CreateHead(Uri uri, RequestOptions options)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, uri);
        ApplyCommon(request, options);
        return request;
    }

    public static HttpRequestMessage CreatePlainGet(Uri uri, RequestOptions options)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyCommon(request, options);
        return request;
    }

    public static HttpRequestMessage CreateSegment(
        Uri uri,
        RequestOptions options,
        long startInclusive,
        long endInclusive,
        ProbeResult probe)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyCommon(request, options);
        request.Headers.Range = new RangeHeaderValue(startInclusive, endInclusive);

        // Only a strong validator is valid in If-Range. Weak ETags must never be used.
        if (probe.HasStrongETag && probe.ETag is not null)
        {
            if (EntityTagHeaderValue.TryParse(probe.ETag, out var tag))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(tag);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("If-Range", probe.ETag);
            }
        }
        else if (probe.LastModified is { } lastModified)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
        }

        return request;
    }

    private static void ApplyCommon(HttpRequestMessage request, RequestOptions options)
    {
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        if (!string.IsNullOrEmpty(options.Referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", options.Referer);
        }

        // Structured cookies travel through the handler's CookieContainer. Sending
        // the raw header as well would duplicate them and bypass domain matching.
        if (options.Cookies is null || options.Cookies.Count == 0)
        {
            if (!string.IsNullOrEmpty(options.Cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", options.Cookie);
            }
        }

        if (options.ExtraHeaders is not null)
        {
            foreach (var header in options.ExtraHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }
}
