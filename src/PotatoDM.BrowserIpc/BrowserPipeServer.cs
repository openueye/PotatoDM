using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PotatoDM.BrowserIpc;

/// <summary>
/// Accepts newline-delimited JSON requests on a current-user pipe. Every
/// connection carries exactly one request and one response.
/// </summary>
public sealed class BrowserPipeServer : IAsyncDisposable
{
    public const int MaxRequestBytes = 256 * 1024;
    public const int MaxResponseBytes = 64 * 1024;

    private readonly string _pipeName;
    private readonly Func<BrowserRequest, CancellationToken, Task<BrowserResponse>> _handler;
    private readonly int _maxConnections;
    private readonly TimeSpan _requestTimeout;
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;
    private int _started;

    public BrowserPipeServer(
        string pipeName,
        Func<BrowserRequest, CancellationToken, Task<BrowserResponse>> handler,
        int maxConnections = 4,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("A pipe name is required.", nameof(pipeName));
        }

        _pipeName = pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _maxConnections = Math.Clamp(maxConnections, 1, 16);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20);
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    // The analyzer cannot follow the hand-off: the connection task owns the stream
    // and disposes it in an await using, while the finally here covers only the
    // failure paths that happen before that hand-off.
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership is transferred to HandleConnectionAsync, which disposes " +
                        "the stream; the finally block covers every pre-hand-off failure.")]
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        _maxConnections,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (IOException)
                {
                    // All instances are currently connected; back off briefly.
                    try
                    {
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    break;
                }

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                // The connection task owns the stream and disposes it; clearing the
                // local records that hand-off so the finally block cannot
                // double-dispose it.
                var connection = server;
                server = null;
                _ = HandleConnectionAsync(connection, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                continue;
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken serverCancellation)
    {
        await using (server)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
            timeout.CancelAfter(_requestTimeout);

            try
            {
                var line = await BoundedLineReader
                    .ReadLineAsync(server, MaxRequestBytes, timeout.Token)
                    .ConfigureAwait(false);

                if (line is null)
                {
                    return;
                }

                BrowserRequest? request;

                try
                {
                    request = JsonSerializer.Deserialize<BrowserRequest>(line, BrowserJson.Options);
                }
                catch (JsonException)
                {
                    await WriteResponseAsync(
                            server,
                            BrowserResponse.Failure("invalid_json", "无法解析请求。"))
                        .ConfigureAwait(false);
                    return;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Type))
                {
                    await WriteResponseAsync(
                            server,
                            BrowserResponse.Failure("invalid_request", "请求缺少类型。"))
                        .ConfigureAwait(false);
                    return;
                }

                BrowserResponse response;

                try
                {
                    response = await _handler(request, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    response = BrowserResponse.Failure("timeout", "客户端处理请求超时。");
                }
                catch (Exception)
                {
                    response = BrowserResponse.Failure("internal_error", "客户端处理请求失败。");
                }

                await WriteResponseAsync(server, response).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (EndOfStreamException)
            {
            }
            catch (InvalidDataException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        BrowserResponse response)
    {
        var json = JsonSerializer.Serialize(response, BrowserJson.Options);
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        var slice = payload.Length <= MaxResponseBytes
            ? payload
            : Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    BrowserResponse.Failure("response_too_large", "响应过大。"),
                    BrowserJson.Options) + "\n");

        await stream.WriteAsync(slice, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
