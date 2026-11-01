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
            catch (Exception ex) when (attempt < retry.MaxRetries && IsRetryable(ex, cancellationToken))
            {
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
            IOException io => io.InnerException is System.Net.Sockets.SocketException,
            _ => false,
        };
    }

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
