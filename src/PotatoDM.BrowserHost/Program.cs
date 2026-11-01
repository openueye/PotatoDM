using System.Diagnostics;
using System.Text.Json;
using PotatoDM.BrowserIpc;

namespace PotatoDM.BrowserHost;

/// <summary>
/// Native messaging host. Reads one request from stdin, forwards it to the
/// running PotatoDM app (launching it when needed) and writes one response.
/// stdout is reserved for the protocol.
/// </summary>
internal static class Program
{
    private const string AppPathVariable = "POTATODM_APP_PATH";
    private const string AppTimeoutVariable = "POTATODM_APP_TIMEOUT_MS";
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    private static async Task<int> Main()
    {
        BrowserRequest? request = null;

        try
        {
            using var input = Console.OpenStandardInput();
            var json = await NativeMessageFraming.ReadAsync(input).ConfigureAwait(false);
            request = JsonSerializer.Deserialize<BrowserRequest>(json, BrowserJson.Options);

            BrowserResponse response;

            if (request is null)
            {
                response = BrowserResponse.Failure("invalid_request", "无法解析原生消息。");
            }
            else if (request.Version != BrowserRequest.CurrentVersion)
            {
                response = BrowserResponse.Failure(
                    "unsupported_version",
                    $"不支持的协议版本 {request.Version}。");
            }
            else
            {
                response = await HandleAsync(request).ConfigureAwait(false);
            }

            using var output = Console.OpenStandardOutput();
            await NativeMessageFraming
                .WriteAsync(output, JsonSerializer.Serialize(response, BrowserJson.Options))
                .ConfigureAwait(false);

            return response.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log("fatal: " + ex.GetType().Name + ": " + ex.Message);

            if (request is not null)
            {
                try
                {
                    using var output = Console.OpenStandardOutput();
                    await NativeMessageFraming.WriteAsync(
                            output,
                            JsonSerializer.Serialize(
                                BrowserResponse.Failure("internal_error", "宿主处理请求失败。"),
                                BrowserJson.Options))
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }

            return 1;
        }
    }

    private static Task<BrowserResponse> HandleAsync(BrowserRequest request)
    {
        return request.Type switch
        {
            BrowserRequestTypes.Ping => PingAsync(),
            BrowserRequestTypes.Activate => ForwardAsync(request, mayLaunchApp: true),
            BrowserRequestTypes.Download => ValidateAndForwardAsync(request),
            _ => Task.FromResult(
                BrowserResponse.Failure("invalid_request", "未知请求类型。")),
        };
    }

    private static async Task<BrowserResponse> ValidateAndForwardAsync(BrowserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url) ||
            !Uri.TryCreate(request.Url, UriKind.Absolute, out var url))
        {
            return BrowserResponse.Failure("invalid_request", "下载地址无效。");
        }

        var isMagnet = string.Equals(request.Kind, "magnet", StringComparison.OrdinalIgnoreCase);
        var isHttp = url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps;

        if (isMagnet ? !url.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase) : !isHttp)
        {
            return BrowserResponse.Failure(
                "invalid_request",
                isMagnet ? "磁力链接无效。" : "仅支持 http/https 下载地址。");
        }

        return await ForwardAsync(request, mayLaunchApp: true).ConfigureAwait(false);
    }

    private static async Task<BrowserResponse> PingAsync()
    {
        var started = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2))
        {
            try
            {
                var response = await TrySendAsync(
                        new BrowserRequest { Type = BrowserRequestTypes.Ping },
                        PerAttemptTimeout,
                        TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);

                if (response is not null)
                {
                    return response;
                }
            }
            catch (Exception ex) when (
                ex is IOException or TimeoutException or OperationCanceledException)
            {
            }

            await Task.Delay(RetryDelay).ConfigureAwait(false);
        }

        return BrowserResponse.Failure(
            "app_unavailable",
            "土豆下载器未运行（开始下载时会自动启动）。");
    }

    private static async Task<BrowserResponse> ForwardAsync(
        BrowserRequest request,
        bool mayLaunchApp)
    {
        var budget = ReadBudget();
        var deadline = DateTimeOffset.UtcNow + budget;
        var launchAttempted = false;
        string? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var connectTimeout = remaining < PerAttemptTimeout ? remaining : PerAttemptTimeout;

            try
            {
                var response = await TrySendAsync(request, connectTimeout, remaining)
                    .ConfigureAwait(false);

                if (response is not null)
                {
                    return response;
                }
            }
            catch (Exception ex) when (
                ex is IOException or TimeoutException or OperationCanceledException)
            {
                lastError = ex.GetType().Name;
            }

            if (mayLaunchApp && !launchAttempted)
            {
                launchAttempted = true;
                LaunchApp();
            }

            try
            {
                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Log("app unavailable: " + (lastError ?? "pipe not reachable"));
        return BrowserResponse.Failure(
            "app_unavailable",
            "无法连接土豆下载器，已交给浏览器下载。");
    }

    private static async Task<BrowserResponse?> TrySendAsync(
        BrowserRequest request,
        TimeSpan connectTimeout,
        TimeSpan responseTimeout)
    {
        await using var client = await BrowserPipeClient
            .ConnectAsync(BrowserPipeNames.ForCurrentSession(), connectTimeout)
            .ConfigureAwait(false);

        return await client.SendAsync(request, responseTimeout).ConfigureAwait(false);
    }

    private static void LaunchApp()
    {
        var path = Environment.GetEnvironmentVariable(AppPathVariable);

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "PotatoDM.exe");
        }

        if (!File.Exists(path))
        {
            Log("app executable not found");
            return;
        }

        try
        {
            // ShellExecuteEx keeps the host's stdio handles out of the app.
            // With UseShellExecute=false the long-lived app would inherit the
            // native messaging pipe, so the browser would not see the host
            // disconnect until the app itself exits.
            var startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
            };

            using var process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log("failed to launch app: " + ex.GetType().Name);
        }
    }

    private static TimeSpan ReadBudget()
    {
        var value = Environment.GetEnvironmentVariable(AppTimeoutVariable);
        if (int.TryParse(value, out var milliseconds))
        {
            return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 1000, 60_000));
        }

        return DefaultBudget;
    }

    private static void Log(string message)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
            {
                return;
            }

            var directory = Path.Combine(local, "PotatoDM", "browser");
            Directory.CreateDirectory(directory);

            var logPath = Path.Combine(directory, "host.log");
            var info = new FileInfo(logPath);
            if (info.Exists && info.Length > 1024 * 1024)
            {
                File.Delete(logPath);
            }

            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never affect the protocol.
        }
    }
}
