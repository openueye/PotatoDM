using System.Net;
using PotatoDM.Core.Models;

namespace PotatoDM.Core.Http;

public static class DownloadHttpClientFactory
{
    /// <summary>
    /// Creates an HTTP client with automatic redirects and decompression disabled.
    /// Redirects are handled manually so the final URI and cookies are observable,
    /// and identity encoding keeps Range offsets meaningful.
    /// </summary>
    public static HttpClient Create(RequestOptions options)
    {
        var cookies = new CookieContainer();
        if (options.Cookies is { Count: > 0 })
        {
            foreach (var cookie in options.Cookies)
            {
                TryAddCookie(cookies, cookie);
            }
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = true,
            CookieContainer = cookies,
            UseProxy = options.UseProxy,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 64,
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
        return client;
    }

    private static void TryAddCookie(CookieContainer container, RequestCookie source)
    {
        if (string.IsNullOrWhiteSpace(source.Name) ||
            string.IsNullOrWhiteSpace(source.Domain))
        {
            return;
        }

        var domain = source.Domain.Trim().TrimStart('.');
        if (domain.Length == 0)
        {
            return;
        }

        var path = string.IsNullOrWhiteSpace(source.Path) ? "/" : source.Path.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        try
        {
            var cookie = new Cookie(source.Name, source.Value ?? string.Empty, path, domain)
            {
                Secure = source.Secure,
                HttpOnly = source.HttpOnly,
            };

            if (source.ExpiresUtc is { } expires)
            {
                cookie.Expires = expires.UtcDateTime;
            }

            var probeUri = new Uri($"{(source.Secure ? "https" : "http")}://{domain}{path}");
            container.Add(probeUri, cookie);
        }
        catch (CookieException)
        {
            // A single malformed browser cookie must not break the download.
        }
        catch (UriFormatException)
        {
        }
        catch (ArgumentException)
        {
        }
    }
}
