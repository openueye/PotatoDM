using System.Diagnostics;
using System.Text.Json;
using PotatoDM.BrowserIpc;

namespace PotatoDM.IntegrationTests;

public class BrowserHostIntegrationTests
{
    [Fact]
    public async Task Host_forwards_a_download_to_the_running_app()
    {
        var pipeName = UniquePipeName();
        await using var server = new BrowserPipeServer(
            pipeName,
            (request, _) => Task.FromResult(
                BrowserResponse.AcceptedDownload(
                    "job-42",
                    Path.Combine("C:", "Downloads", "a.bin"))));
        server.Start();

        var (exitCode, response) = await RunHostAsync(
            pipeName,
            new BrowserRequest
            {
                Type = BrowserRequestTypes.Download,
                RequestId = "host-test-1",
                Url = "https://example.test/a.bin",
                SuggestedFileName = "a.bin",
            },
            budgetMilliseconds: 5000);

        Assert.Equal(0, exitCode);
        Assert.True(response.Ok);
        Assert.True(response.Accepted);
        Assert.Equal("job-42", response.JobId);
        Assert.Equal(Path.Combine("C:", "Downloads", "a.bin"), response.TargetPath);
    }

    [Fact]
    public async Task Host_fails_fast_when_the_app_is_unavailable()
    {
        var stopwatch = Stopwatch.StartNew();

        var (exitCode, response) = await RunHostAsync(
            UniquePipeName(),
            new BrowserRequest
            {
                Type = BrowserRequestTypes.Download,
                Url = "https://example.test/a.bin",
            },
            budgetMilliseconds: 1500,
            appPath: Path.Combine(AppContext.BaseDirectory, "does-not-exist.exe"));

        stopwatch.Stop();

        Assert.Equal(1, exitCode);
        Assert.False(response.Ok);
        Assert.Equal("app_unavailable", response.Error);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"host took {stopwatch.Elapsed} to give up");
    }

    [Fact]
    public async Task Host_rejects_non_http_urls_without_touching_the_pipe()
    {
        var (exitCode, response) = await RunHostAsync(
            UniquePipeName(),
            new BrowserRequest
            {
                Type = BrowserRequestTypes.Download,
                Url = "ftp://example.test/a.bin",
            },
            budgetMilliseconds: 1500);

        Assert.Equal(1, exitCode);
        Assert.False(response.Ok);
        Assert.Equal("invalid_request", response.Error);
    }

    private static async Task<(int ExitCode, BrowserResponse Response)> RunHostAsync(
        string pipeName,
        BrowserRequest request,
        int budgetMilliseconds,
        string? appPath = null)
    {
        var hostPath = Path.Combine(AppContext.BaseDirectory, "host", "PotatoDM.BrowserHost.exe");
        Assert.True(File.Exists(hostPath), $"Browser host was not copied to '{hostPath}'.");

        var information = new ProcessStartInfo(hostPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        information.Environment[BrowserPipeNames.PipeNameOverrideVariable] = pipeName;
        information.Environment["POTATODM_APP_TIMEOUT_MS"] =
            budgetMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        information.Environment["POTATODM_APP_PATH"] =
            appPath ?? Path.Combine(AppContext.BaseDirectory, "no-app.exe");

        using var process = new Process { StartInfo = information };
        Assert.True(process.Start());

        var json = JsonSerializer.Serialize(request, BrowserJson.Options);
        await NativeMessageFraming.WriteAsync(process.StandardInput.BaseStream, json);
        process.StandardInput.Close();

        var responseJson = await NativeMessageFraming
            .ReadAsync(process.StandardOutput.BaseStream)
            .WaitAsync(TimeSpan.FromSeconds(40));

        var response = JsonSerializer.Deserialize<BrowserResponse>(
                           responseJson,
                           BrowserJson.Options)
                       ?? throw new InvalidOperationException("The host returned an empty response.");

        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(exitTimeout.Token);

        return (process.ExitCode, response);
    }

    private static string UniquePipeName() =>
        "PotatoDM.Tests.Host." + Guid.NewGuid().ToString("N");
}
