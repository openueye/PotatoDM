using System.Net;
using System.Net.Http.Headers;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Http;
using PotatoDM.Core.Models;
using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Tests;

public class DownloadRequestFactoryTests
{
    private static readonly Uri Url = new("http://127.0.0.1:5099/dl?token=test");

    [Fact]
    public void Probe_requests_a_single_byte_range_and_identity_encoding()
    {
        using var request = DownloadRequestFactory.CreateProbe(Url, new RequestOptions());

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("bytes=0-0", request.Headers.Range!.ToString());
        Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).ToString());
    }

    [Fact]
    public void Segment_request_carries_the_exact_range()
    {
        var probe = new ProbeResult(Url, Url, 1000, true, "\"v1\"", null, "f.bin", null);

        using var request = DownloadRequestFactory.CreateSegment(
            Url, new RequestOptions(), 100, 199, probe);

        Assert.Equal("bytes=100-199", request.Headers.Range!.ToString());
    }

    [Fact]
    public void Strong_etag_becomes_if_range()
    {
        var probe = new ProbeResult(Url, Url, 1000, true, "\"v1\"", null, "f.bin", null);

        using var request = DownloadRequestFactory.CreateSegment(
            Url, new RequestOptions(), 0, 99, probe);

        Assert.Equal("\"v1\"", request.Headers.IfRange!.EntityTag!.Tag);
    }

    [Fact]
    public void Weak_etag_is_never_used_for_if_range()
    {
        var lastModified = new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var probe = new ProbeResult(
            Url, Url, 1000, true, "W/\"weak\"", lastModified, "f.bin", null);

        using var request = DownloadRequestFactory.CreateSegment(
            Url, new RequestOptions(), 0, 99, probe);

        Assert.NotNull(request.Headers.IfRange);
        Assert.Null(request.Headers.IfRange!.EntityTag);
        Assert.Equal(lastModified, request.Headers.IfRange.Date);
    }

    [Fact]
    public void No_validator_means_no_if_range()
    {
        var probe = new ProbeResult(Url, Url, 1000, true, "W/\"weak\"", null, "f.bin", null);

        using var request = DownloadRequestFactory.CreateSegment(
            Url, new RequestOptions(), 0, 99, probe);

        Assert.Null(request.Headers.IfRange);
    }

    [Fact]
    public void Common_headers_are_applied()
    {
        var options = new RequestOptions
        {
            Referer = "https://example.test/page",
            Cookie = "session=abc",
            ExtraHeaders = new Dictionary<string, string> { ["X-Test"] = "1" },
        };

        using var request = DownloadRequestFactory.CreatePlainGet(Url, options);

        Assert.Equal("https://example.test/page", request.Headers.Referrer!.ToString());
        Assert.Equal("session=abc", request.Headers.GetValues("Cookie").Single());
        Assert.Equal("1", request.Headers.GetValues("X-Test").Single());
        Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).ToString());
    }

    [Fact]
    public void Strong_etag_predicate_matches_only_strong_tags()
    {
        var strong = new ProbeResult(Url, Url, 1, true, "\"a\"", null, null, null);
        var weak = new ProbeResult(Url, Url, 1, true, "W/\"a\"", null, null, null);

        Assert.True(strong.HasStrongETag);
        Assert.False(weak.HasStrongETag);
    }
}

public class UrlProbeTests
{
    private static readonly Uri Url = new("http://127.0.0.1:5099/dl?token=probe");
    private static readonly UrlProbe Probe = new(new RetryPolicy(maxRetries: 0));

    private static HttpClient Client(StubHandler handler) =>
        new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    [Fact]
    public async Task Partial_content_reports_length_ranges_and_validators()
    {
        var handler = new StubHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 0 }),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 12345);
            response.Content.Headers.ContentLength = 1;
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            response.Content.Headers.LastModified =
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            response.Content.Headers.TryAddWithoutValidation(
                "Content-Disposition",
                "attachment; filename=\"fallback.bin\"; filename*=UTF-8''%E4%B8%AD%E6%96%87.bin");
            return response;
        });

        using var client = Client(handler);
        var result = await Probe.ProbeAsync(client, Url, new RequestOptions(), default);

        Assert.Equal(12345, result.TotalLength);
        Assert.True(result.SupportsRanges);
        Assert.Equal("\"v1\"", result.ETag);
        Assert.Equal("中文.bin", result.SuggestedFileName);
        Assert.Equal("bytes=0-0", Assert.Single(handler.Requests).Headers.Range!.ToString());
    }

    [Fact]
    public async Task Plain_200_means_ranges_are_not_supported()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
            response.Content.Headers.ContentLength = 500;
            return response;
        });

        using var client = Client(handler);
        var result = await Probe.ProbeAsync(client, Url, new RequestOptions(), default);

        Assert.Equal(500, result.TotalLength);
        Assert.False(result.SupportsRanges);
    }

    [Fact]
    public async Task Unsatisfiable_range_still_reveals_total_length()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(9876);
            return response;
        });

        using var client = Client(handler);
        var result = await Probe.ProbeAsync(client, Url, new RequestOptions(), default);

        Assert.Equal(9876, result.TotalLength);
        Assert.True(result.SupportsRanges);
    }

    [Fact]
    public async Task Method_not_allowed_falls_back_to_head()
    {
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                head.Content.Headers.ContentLength = 777;
                head.Headers.TryAddWithoutValidation("Accept-Ranges", "bytes");
                return head;
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
        });

        using var client = Client(handler);
        var result = await Probe.ProbeAsync(client, Url, new RequestOptions(), default);

        Assert.Equal(777, result.TotalLength);
        Assert.True(result.SupportsRanges);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Head },
            handler.Requests.Select(r => r.Method).ToArray());
    }

    [Fact]
    public async Task Rejected_head_falls_back_to_plain_get()
    {
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Head || request.Headers.Range is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
            response.Content.Headers.ContentLength = 888;
            return response;
        });

        using var client = Client(handler);
        var result = await Probe.ProbeAsync(client, Url, new RequestOptions(), default);

        Assert.Equal(888, result.TotalLength);
        Assert.False(result.SupportsRanges);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Null(handler.Requests[2].Headers.Range);
    }

    [Fact]
    public async Task Redirects_are_followed_and_the_final_uri_is_reported()
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

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 0 }),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 4242);
            return response;
        });

        using var client = Client(handler);
        var result = await Probe.ProbeAsync(client, Url, new RequestOptions(), default);

        Assert.Equal(4242, result.TotalLength);
        Assert.Equal("/real", result.FinalUri.AbsolutePath);
        Assert.Equal("/real", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("bytes=0-0", handler.Requests[1].Headers.Range!.ToString());
    }

    [Fact]
    public async Task Compressed_response_is_rejected()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
            };
            response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        });

        using var client = Client(handler);

        await Assert.ThrowsAsync<UnsupportedContentEncodingException>(
            () => Probe.ProbeAsync(client, Url, new RequestOptions(), default));
    }

    [Fact]
    public async Task Not_found_is_a_permanent_failure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        });

        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<HttpDownloadException>(
            () => Probe.ProbeAsync(client, Url, new RequestOptions(), default));

        Assert.False(error.IsTransient);
        Assert.Equal(DownloadExitCode.HttpPermanentFailure, error.ExitCode);
    }

    [Fact]
    public async Task Server_error_is_retryable()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        });

        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<HttpDownloadException>(
            () => Probe.ProbeAsync(client, Url, new RequestOptions(), default));

        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Redirect_loop_is_bounded()
    {
        var handler = new StubHandler(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
            redirect.Headers.Location = new Uri("/loop", UriKind.Relative);
            return redirect;
        });

        using var client = Client(handler);

        await Assert.ThrowsAsync<HttpDownloadException>(
            () => Probe.ProbeAsync(client, Url, new RequestOptions(), default));
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var response = _responder(request);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }
}
