using PotatoDM.AppCore.Job;
using PotatoDM.AppCore.Models;
using PotatoDM.AppCore.Services;
using PotatoDM.BrowserIpc;
using PotatoDM.Core.Models;

namespace PotatoDM.App.Services;

/// <summary>Translates untrusted browser messages into manager calls on the UI thread.</summary>
public sealed class BrowserPipeHandler
{
    private const int MaxCookies = 512;
    private const int MaxCookieBytes = 32 * 1024;
    private const int MaxHeaderValueLength = 8 * 1024;
    private const int MaxFieldLength = 4096;

    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Language",
        "DNT",
        "Upgrade-Insecure-Requests",
    };

    private readonly DownloadManager _manager;
    private readonly IUiDispatcher _ui;
    private readonly Action _activate;

    public BrowserPipeHandler(DownloadManager manager, IUiDispatcher ui, Action activate)
    {
        _manager = manager;
        _ui = ui;
        _activate = activate;
    }

    public async Task<BrowserResponse> HandleAsync(
        BrowserRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case BrowserRequestTypes.Ping:
                return BrowserResponse.Success("pong");

            case BrowserRequestTypes.Activate:
                await _ui.InvokeAsync(() =>
                {
                    _activate();
                    return Task.CompletedTask;
                });

                return BrowserResponse.Success();

            case BrowserRequestTypes.Download:
                return await HandleDownloadAsync(request, cancellationToken);

            default:
                return BrowserResponse.Failure("invalid_request", "未知请求类型。");
        }
    }

    private async Task<BrowserResponse> HandleDownloadAsync(
        BrowserRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Url) ||
            !Uri.TryCreate(request.Url, UriKind.Absolute, out var url))
        {
            return BrowserResponse.Failure("invalid_request", "下载地址无效。");
        }

        var kind = NormalizeKind(request.Kind);
        var isMagnet = kind == "magnet";

        if (isMagnet
                ? !url.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)
                : url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return BrowserResponse.Failure(
                "invalid_request",
                isMagnet ? "磁力链接无效。" : "仅支持 http/https 下载地址。");
        }

        var context = new BrowserDownloadContext
        {
            Url = url,
            Kind = kind,
            ContentType = SanitizeHeaderValue(request.ContentType, 200),
            RequestId = Clamp(request.RequestId, 200),
            SuggestedFileName = Clamp(request.SuggestedFileName, 1024),
            Referer = SanitizeOptionalUrl(request.Referer),
            UserAgent = SanitizeHeaderValue(request.UserAgent, 1024),
            Cookies = SanitizeCookies(request.Cookies),
            ExtraHeaders = SanitizeHeaders(request.ExtraHeaders),
        };

        // The manager mutates observable job state and must run on the UI thread.
        var result = await _ui.InvokeAsync(
            () => _manager.AddFromBrowserAsync(context, cancellationToken));

        return result.Accepted
            ? BrowserResponse.AcceptedDownload(
                result.JobId ?? string.Empty,
                result.TargetPath ?? string.Empty)
            : BrowserResponse.Rejected(
                result.Error ?? "internal_error",
                result.Message);
    }

    private static string? NormalizeKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "manifest" => "manifest",
            "magnet" => "magnet",
            _ => null,
        };

    private static List<RequestCookie>? SanitizeCookies(
        IReadOnlyList<BrowserCookieDto>? cookies)
    {
        if (cookies is null || cookies.Count == 0)
        {
            return null;
        }

        var result = new List<RequestCookie>();
        var totalBytes = 0;

        foreach (var cookie in cookies.Take(MaxCookies))
        {
            var name = Clamp(cookie.Name, 256) ?? string.Empty;
            var value = Clamp(cookie.Value, MaxFieldLength) ?? string.Empty;
            var domain = Clamp(cookie.Domain, 253) ?? string.Empty;
            var path = Clamp(cookie.Path, 1024) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(domain))
            {
                continue;
            }

            if (ContainsControlCharacters(name) ||
                ContainsControlCharacters(value) ||
                ContainsControlCharacters(domain) ||
                ContainsControlCharacters(path))
            {
                continue;
            }

            totalBytes += name.Length + value.Length;
            if (totalBytes > MaxCookieBytes)
            {
                break;
            }

            result.Add(new RequestCookie
            {
                Name = name,
                Value = value,
                Domain = domain,
                Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                HostOnly = cookie.HostOnly,
                ExpiresUtc = ToExpiration(cookie),
            });
        }

        return result.Count == 0 ? null : result;
    }

    private static DateTimeOffset? ToExpiration(BrowserCookieDto cookie)
    {
        if (cookie.Session || cookie.ExpirationDate is not { } seconds)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static Dictionary<string, string>? SanitizeHeaders(
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers.Take(8))
        {
            var value = SanitizeHeaderValue(header.Value, MaxHeaderValueLength);
            if (value is not null && AllowedHeaders.Contains(header.Key))
            {
                result[header.Key] = value;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static string? SanitizeHeaderValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var clamped = Clamp(value, maxLength);
        return clamped is not null && !ContainsControlCharacters(clamped) ? clamped : null;
    }

    private static string? SanitizeOptionalUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return Clamp(value, MaxFieldLength);
    }

    private static string? Clamp(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static bool ContainsControlCharacters(string value)
    {
        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7F)
            {
                return true;
            }
        }

        return false;
    }
}
