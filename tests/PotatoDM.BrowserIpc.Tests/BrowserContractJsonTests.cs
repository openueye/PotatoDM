using System.Text.Json;
using PotatoDM.BrowserIpc;

namespace PotatoDM.BrowserIpc.Tests;

public sealed class BrowserContractJsonTests
{
    [Fact]
    public void DownloadRequest_UsesRefererWireField()
    {
        // This is the exact shape browser/shared/background.js sends.
        const string json = """
            {
              "version": 1,
              "type": "download",
              "requestId": "r1",
              "url": "https://example.test/file.bin",
              "suggestedFileName": "file.bin",
              "referer": "https://example.test/page",
              "userAgent": "PotatoTest/1.0",
              "cookies": [],
              "extraHeaders": { "Accept": "*/*" }
            }
            """;

        var request = JsonSerializer.Deserialize<BrowserRequest>(json, BrowserJson.Options);

        Assert.NotNull(request);
        Assert.Equal(BrowserRequest.CurrentVersion, request.Version);
        Assert.Equal(BrowserRequestTypes.Download, request.Type);
        Assert.Equal("r1", request.RequestId);
        Assert.Equal("https://example.test/page", request.Referer);
        Assert.Equal("PotatoTest/1.0", request.UserAgent);
        Assert.NotNull(request.ExtraHeaders);
        Assert.Equal("*/*", request.ExtraHeaders!["Accept"]);
    }

    [Fact]
    public void Request_SerializesRefererAsReferer()
    {
        var request = new BrowserRequest
        {
            Type = BrowserRequestTypes.Download,
            Url = "https://example.test/file.bin",
            Referer = "https://example.test/page",
        };

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(request, BrowserJson.Options));

        Assert.True(document.RootElement.TryGetProperty("referer", out var referer));
        Assert.Equal("https://example.test/page", referer.GetString());
        Assert.False(document.RootElement.TryGetProperty("referrer", out _));
    }
}
