using System.Net;
using PotatoDM.Core.Http;
using PotatoDM.Core.Models;
using PotatoDM.Core.Storage;

namespace PotatoDM.Core.Tests;

public class StructuredCookieRequestTests
{
    private static readonly Uri Url = new("http://127.0.0.1:5099/dl?token=test");

    [Fact]
    public void Structured_cookies_suppress_the_raw_cookie_header()
    {
        var options = new RequestOptions
        {
            Cookie = "legacy=1",
            Cookies = new[]
            {
                new RequestCookie
                {
                    Name = "session",
                    Value = "abc",
                    Domain = "example.test",
                    Path = "/",
                },
            },
        };

        using var request = DownloadRequestFactory.CreatePlainGet(Url, options);

        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Fact]
    public void Raw_cookie_header_is_still_used_without_structured_cookies()
    {
        var options = new RequestOptions
        {
            Cookie = "legacy=1",
            Cookies = Array.Empty<RequestCookie>(),
        };

        using var request = DownloadRequestFactory.CreatePlainGet(Url, options);

        Assert.Equal("legacy=1", request.Headers.GetValues("Cookie").Single());
    }
}

public class RedirectHeaderTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:5099/dl");

    private static HttpClient Client(StubHandler handler) =>
        new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    private static RequestOptions Options() => new()
    {
        Cookie = "session=abc",
        ExtraHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer token",
            ["Proxy-Authorization"] = "Basic abc",
            ["X-Test"] = "1",
        },
    };

    [Fact]
    public async Task Cross_origin_redirect_drops_cookies_and_credentials()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == Origin.Host)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                redirect.Headers.Location = new Uri("http://other.example/real");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
        });

        using var client = Client(handler);
        using var request = DownloadRequestFactory.CreatePlainGet(Origin, Options());

        var (response, finalUri) = await HttpSender.SendAsync(client, request, default);
        response.Dispose();

        Assert.Equal("http://other.example/real", finalUri.AbsoluteUri);

        var first = handler.Requests[0];
        Assert.Equal("session=abc", first.Headers.GetValues("Cookie").Single());
        Assert.Equal("Bearer token", first.Headers.GetValues("Authorization").Single());

        var second = handler.Requests[1];
        Assert.False(second.Headers.Contains("Cookie"));
        Assert.False(second.Headers.Contains("Authorization"));
        Assert.False(second.Headers.Contains("Proxy-Authorization"));
        Assert.Equal("1", second.Headers.GetValues("X-Test").Single());
    }

    [Fact]
    public async Task Same_origin_redirect_keeps_cookies_and_credentials()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/dl")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                redirect.Headers.Location = new Uri("/real", UriKind.Relative);
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
        });

        using var client = Client(handler);
        using var request = DownloadRequestFactory.CreatePlainGet(Origin, Options());

        var (response, finalUri) = await HttpSender.SendAsync(client, request, default);
        response.Dispose();

        Assert.Equal("/real", finalUri.AbsolutePath);

        var second = handler.Requests[1];
        Assert.Equal("session=abc", second.Headers.GetValues("Cookie").Single());
        Assert.Equal("Bearer token", second.Headers.GetValues("Authorization").Single());
    }
}

public class ArtifactAwareResolverTests
{
    [Fact]
    public void Sidecars_count_as_occupied_only_when_requested()
    {
        using var temp = new TempDirectory();
        var sidecar = temp.FileFor("report.bin" + StatePaths.StateSuffix);
        File.WriteAllText(sidecar, "{}");

        Assert.Equal(
            temp.FileFor("report.bin"),
            TargetPathResolver.Resolve(temp.Path, "report.bin"));

        Assert.Equal(
            temp.FileFor("report (1).bin"),
            TargetPathResolver.Resolve(temp.Path, "report.bin", avoidExistingArtifacts: true));
    }

    [Fact]
    public void Reserved_targets_are_skipped()
    {
        using var temp = new TempDirectory();
        var reserved = temp.FileFor("report.bin");

        var resolved = TargetPathResolver.Resolve(
            temp.Path,
            "report.bin",
            avoidExistingArtifacts: true,
            isReserved: path => string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(reserved),
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(temp.FileFor("report (1).bin"), resolved);
    }

    [Fact]
    public void Working_files_also_count_as_occupied()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.FileFor("movie.mkv" + StatePaths.WorkingSuffix), "partial");

        Assert.Equal(
            temp.FileFor("movie (1).mkv"),
            TargetPathResolver.Resolve(temp.Path, "movie.mkv", avoidExistingArtifacts: true));
    }
}
