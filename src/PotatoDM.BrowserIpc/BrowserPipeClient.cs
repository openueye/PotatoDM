using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PotatoDM.BrowserIpc;

/// <summary>One-shot client used by the native host to reach the running app.</summary>
public sealed class BrowserPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _stream;

    private BrowserPipeClient(NamedPipeClientStream stream)
    {
        _stream = stream;
    }

    public static async Task<BrowserPipeClient> ConnectAsync(
        string pipeName,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("A pipe name is required.", nameof(pipeName));
        }

        var stream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectTimeout);

        try
        {
            await stream.ConnectAsync(timeout.Token).ConfigureAwait(false);
            return new BrowserPipeClient(stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<BrowserResponse> SendAsync(
        BrowserRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var json = JsonSerializer.Serialize(request, BrowserJson.Options);
        var payload = Encoding.UTF8.GetBytes(json + "\n");

        if (payload.Length > BrowserPipeServer.MaxRequestBytes)
        {
            throw new InvalidDataException("The request is too large for the app pipe.");
        }

        await _stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
        await _stream.FlushAsync(deadline.Token).ConfigureAwait(false);

        var line = await BoundedLineReader
            .ReadLineAsync(_stream, BrowserPipeServer.MaxResponseBytes, deadline.Token)
            .ConfigureAwait(false);

        if (line is null)
        {
            throw new IOException("The app pipe closed before sending a response.");
        }

        return JsonSerializer.Deserialize<BrowserResponse>(line, BrowserJson.Options)
               ?? throw new InvalidDataException("The app pipe returned an empty response.");
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
