using System.Net;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Http;
using PotatoDM.Core.Models;
using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Media;

internal static class MediaHttp
{
    private const long MaxManifestBytes = 32L * 1024 * 1024;

    internal static async Task<string> FetchTextAsync(
        HttpClient client,
        Uri uri,
        RequestOptions requestOptions,
        RetryPolicy retry,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = DownloadRequestFactory.CreatePlainGet(uri, requestOptions);
                var (response, _) = await HttpSender.SendAsync(client, request, cancellationToken)
                    .ConfigureAwait(false);

                using (response)
                {
                    if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                    {
                        throw CreateHttpException(response, uri);
                    }

                    EnsureIdentityEncoding(response);

                    if (response.Content.Headers.ContentLength is > MaxManifestBytes)
                    {
                        throw new MediaManifestException($"The manifest at '{uri}' is too large.");
                    }

                    var text = await response.Content.ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (text.Length == 0)
                    {
                        throw new MediaManifestException($"The manifest at '{uri}' is empty.");
                    }

                    return text;
                }
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken))
            {
                if (attempt >= retry.MaxRetries)
                {
                    throw RetryExhausted(ex, uri, attempt + 1);
                }

                await Task.Delay(retry.GetDelay(attempt, null), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsRetryable(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            HttpDownloadException http => http.IsTransient,
            HttpRequestException => true,
            // HttpClient reports its own timeout as TaskCanceledException. The
            // cancellation check above separates it from a user cancel.
            TaskCanceledException => true,
            IOException io => io.InnerException is System.Net.Sockets.SocketException,
            _ => false,
        };
    }

    /// <summary>
    /// Converts a failure that survived every retry into the shared exit-code
    /// classification. The direct-download path already reports an unreachable
    /// host as 9; without this the media path fell through to 1 (unclassified).
    /// </summary>
    internal static HttpDownloadException RetryExhausted(Exception error, Uri uri, int attempts) =>
        error as HttpDownloadException
        ?? new HttpDownloadException(
            $"Media request for '{uri}' failed after {attempts} attempts.",
            isTransient: true,
            error);

    internal static HttpDownloadException CreateHttpException(HttpResponseMessage response, Uri uri)
    {
        var status = (int)response.StatusCode;
        var retryAfter = RetryAfterParser.Parse(response);
        var exception = new HttpDownloadException(
            $"Media request for '{uri}' failed with HTTP {status} ({response.StatusCode}).",
            RetryPolicy.IsRetryableStatus(response.StatusCode));

        if (retryAfter is { } delay)
        {
            exception.Data["RetryAfter"] = delay;
        }

        return exception;
    }

    internal static void EnsureIdentityEncoding(HttpResponseMessage response)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count > 0 &&
            encodings.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnsupportedContentEncodingException(string.Join(", ", encodings));
        }
    }
}
