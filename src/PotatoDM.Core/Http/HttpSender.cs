using System.Net;
using PotatoDM.Core.Exceptions;

namespace PotatoDM.Core.Http;

public static class HttpSender
{
    private const int MaxRedirects = 10;

    /// <summary>
    /// Sends a request, following redirects manually while preserving headers.
    /// The returned final URI is what a browser would have ended up on.
    /// </summary>
    public static async Task<(HttpResponseMessage Response, Uri FinalUri)> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri
                  ?? throw new ArgumentException("Request URI is required.", nameof(request));
        var method = request.Method;
        var headers = request.Headers
            .Select(h => new KeyValuePair<string, string[]>(h.Key, h.Value.ToArray()))
            .ToArray();

        var current = request;
        var ownsCurrent = false;

        // The hop being built. It only leaves this method's ownership when the
        // request has been sent and the loop moves on to the next redirect.
        HttpRequestMessage? pending = null;

        try
        {
            for (var redirects = 0; ; redirects++)
            {
                var response = await client
                    .SendAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!IsRedirect(response.StatusCode))
                {
                    return (response, uri);
                }

                if (redirects >= MaxRedirects)
                {
                    response.Dispose();
                    throw new HttpDownloadException("Too many redirects (limit 10).", isTransient: false);
                }

                var location = response.Headers.Location;
                response.Dispose();

                if (location is null)
                {
                    throw new HttpDownloadException("Redirect response did not include a Location header.",
                        isTransient: false);
                }

                var previousUri = uri;
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);

                // Cookies and credentials must not follow a cross-origin redirect.
                var sameOrigin = string.Equals(
                                     previousUri.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(
                                     previousUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase) &&
                                 previousUri.Port == uri.Port;

                pending = new HttpRequestMessage(method, uri);
                foreach (var header in headers)
                {
                    if (!sameOrigin && IsSensitiveHeader(header.Key))
                    {
                        continue;
                    }

                    pending.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                // The caller owns the request it passed in; every hop this method
                // builds is ours to release once SendAsync has finished with it.
                if (ownsCurrent)
                {
                    current.Dispose();
                }

                current = pending;
                pending = null;
                ownsCurrent = true;
            }
        }
        finally
        {
            // A hop that was built but never handed to SendAsync (for example a
            // header copy that throws) is still ours.
            pending?.Dispose();

            if (ownsCurrent)
            {
                current.Dispose();
            }
        }
    }

    private static bool IsSensitiveHeader(string name) =>
        name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
