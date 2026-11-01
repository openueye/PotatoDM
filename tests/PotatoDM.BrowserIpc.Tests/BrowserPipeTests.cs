using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PotatoDM.BrowserIpc.Tests;

public sealed class BrowserPipeTests
{
    [Fact]
    public void ForSessionId_UsesVersionedSessionScopedName()
    {
        Assert.Equal("PotatoDM.Browser.v1.42", BrowserPipeNames.ForSessionId(42));
    }

    [Fact]
    public async Task ServerAndClient_RoundTripARequest()
    {
        var pipeName = UniquePipeName();
        await using var server = new BrowserPipeServer(
            pipeName,
            (_, _) => Task.FromResult(
                BrowserResponse.AcceptedDownload("job-1", @"C:\Downloads\file.bin")));
        server.Start();

        await using var client = await BrowserPipeClient.ConnectAsync(
            pipeName,
            TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(
            new BrowserRequest
            {
                Type = BrowserRequestTypes.Download,
                Url = "https://example.com/file.bin",
            },
            TimeSpan.FromSeconds(5));

        Assert.True(response.Ok);
        Assert.True(response.Accepted);
        Assert.Equal("job-1", response.JobId);
        Assert.Equal(@"C:\Downloads\file.bin", response.TargetPath);
    }

    [Fact]
    public async Task Server_ServesConcurrentClients()
    {
        var pipeName = UniquePipeName();
        await using var server = new BrowserPipeServer(
            pipeName,
            async (request, cancellationToken) =>
            {
                await Task.Delay(50, cancellationToken);
                return BrowserResponse.Success(request.RequestId);
            });
        server.Start();

        var tasks = Enumerable.Range(0, 4).Select(async index =>
        {
            await using var client = await BrowserPipeClient.ConnectAsync(
                pipeName,
                TimeSpan.FromSeconds(5));

            var response = await client.SendAsync(
                new BrowserRequest
                {
                    Type = BrowserRequestTypes.Ping,
                    RequestId = "request-" + index,
                },
                TimeSpan.FromSeconds(5));

            Assert.True(response.Ok);
            Assert.Equal("request-" + index, response.Message);
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Server_MapsMalformedJsonToInvalidJson()
    {
        var pipeName = UniquePipeName();
        await using var server = new BrowserPipeServer(
            pipeName,
            (_, _) => Task.FromResult(BrowserResponse.Success()));
        server.Start();

        using var raw = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await raw.ConnectAsync(5000);
        var bytes = Encoding.UTF8.GetBytes("this is not json\n");
        await raw.WriteAsync(bytes);
        await raw.FlushAsync();

        using var reader = new StreamReader(raw, Encoding.UTF8);
        var line = await reader.ReadLineAsync();

        var response = JsonSerializer.Deserialize<BrowserResponse>(line!, BrowserJson.Options);
        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.Equal("invalid_json", response.Error);
    }

    [Fact]
    public async Task Server_RejectsMissingRequestType()
    {
        var pipeName = UniquePipeName();
        await using var server = new BrowserPipeServer(
            pipeName,
            (_, _) => Task.FromResult(BrowserResponse.Success()));
        server.Start();

        await using var client = await BrowserPipeClient.ConnectAsync(
            pipeName,
            TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(
            new BrowserRequest { Type = "   " },
            TimeSpan.FromSeconds(5));

        Assert.False(response.Ok);
        Assert.Equal("invalid_request", response.Error);
    }

    [Fact]
    public async Task Server_MapsHandlerExceptionsToInternalError()
    {
        var pipeName = UniquePipeName();
        await using var server = new BrowserPipeServer(
            pipeName,
            (_, _) => throw new InvalidOperationException("boom"));
        server.Start();

        await using var client = await BrowserPipeClient.ConnectAsync(
            pipeName,
            TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(
            new BrowserRequest { Type = BrowserRequestTypes.Ping },
            TimeSpan.FromSeconds(5));

        Assert.False(response.Ok);
        Assert.Equal("internal_error", response.Error);
    }

    [Fact]
    public async Task Server_MapsHandlerTimeoutsToTimeout()
    {
        var pipeName = UniquePipeName();
        await using var server = new BrowserPipeServer(
            pipeName,
            async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                return BrowserResponse.Success();
            },
            requestTimeout: TimeSpan.FromMilliseconds(200));
        server.Start();

        await using var client = await BrowserPipeClient.ConnectAsync(
            pipeName,
            TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(
            new BrowserRequest { Type = BrowserRequestTypes.Ping },
            TimeSpan.FromSeconds(5));

        Assert.False(response.Ok);
        Assert.Equal("timeout", response.Error);
    }

    [Fact]
    public async Task Client_ConnectFailsAfterServerStops()
    {
        var pipeName = UniquePipeName();
        var server = new BrowserPipeServer(
            pipeName,
            (_, _) => Task.FromResult(BrowserResponse.Success()));
        server.Start();

        await using (var client = await BrowserPipeClient.ConnectAsync(
                         pipeName,
                         TimeSpan.FromSeconds(5)))
        {
            var response = await client.SendAsync(
                new BrowserRequest { Type = BrowserRequestTypes.Ping },
                TimeSpan.FromSeconds(5));
            Assert.True(response.Ok);
        }

        await server.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
            {
                await using var client = await BrowserPipeClient.ConnectAsync(
                    pipeName,
                    TimeSpan.FromMilliseconds(300));
            });
    }

    private static string UniquePipeName() =>
        "PotatoDM.Tests." + Guid.NewGuid().ToString("N");
}
