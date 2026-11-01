using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;

namespace PotatoDM.TestServer;

public static class DownloadEndpoints
{
    private const long DefaultSize = 8L * 1024 * 1024;

    public static void Map(WebApplication app)
    {
        app.MapMethods("/dl", new[] { "GET", "HEAD" }, HandleDownloadAsync);
        app.MapGet("/checksum", HandleChecksum);
        app.MapGet("/admin/log", HandleLog);
        app.MapGet("/admin/stats", HandleStats);
        app.MapMethods("/admin/config", new[] { "GET", "POST" }, HandleConfig);
        app.MapMethods("/admin/reset", new[] { "GET", "POST" }, HandleReset);
        app.MapGet("/cookie/start", HandleCookieStart);
        app.MapGet("/chunked", HandleChunkedAsync);
        app.MapGet("/health", static () => Results.Text("ok"));
    }

    private static async Task HandleDownloadAsync(HttpContext context, TokenStore store)
    {
        var token = Query(context.Request, "token") ?? "default";
        var state = store.Get(token);

        var log = new RequestLog
        {
            Index = state.NextRequestIndex(),
            Method = context.Request.Method,
            Path = context.Request.Path + context.Request.QueryString,
            Range = context.Request.Headers.Range.ToString(),
            IfRange = context.Request.Headers.IfRange.ToString(),
            Cookie = context.Request.Headers.Cookie.ToString(),
            StartedUtc = DateTimeOffset.UtcNow,
        };

        state.Logs.Enqueue(log);
        state.Enter();

        try
        {
            await HandleDownloadCoreAsync(context, state, log);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Expected for stall/disconnect/abort tests.
        }
        finally
        {
            log.Status = context.Response.StatusCode;
            log.EndedUtc = DateTimeOffset.UtcNow;
            state.Exit();
        }
    }

    private static async Task HandleDownloadCoreAsync(
        HttpContext context,
        TokenState state,
        RequestLog log)
    {
        var request = context.Request;
        var response = context.Response;

        if (Query(request, "cookie") == "required" && request.Cookies["session"] != "ok")
        {
            response.Redirect("/cookie/start" + request.QueryString);
            return;
        }

        // Unlike "required" (which sets the cookie and redirects), "strict"
        // rejects the request so tests can prove a cookie was actually sent.
        if (Query(request, "cookie") == "strict")
        {
            var expected = Query(request, "cookieValue") ?? "ok";
            if (request.Cookies["session"] != expected)
            {
                response.StatusCode = StatusCodes.Status401Unauthorized;
                response.ContentLength = 0;
                return;
            }
        }

        var redirects = QueryInt(request, "redirect", 0);
        if (redirects > 0)
        {
            var parameters = request.Query.ToDictionary(
                pair => pair.Key,
                pair => (string?)pair.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);
            parameters["redirect"] = (redirects - 1).ToString(CultureInfo.InvariantCulture);
            response.Redirect("/dl" + QueryString.Create(parameters));
            return;
        }

        var size = QueryLong(request, "size", DefaultSize);
        if (size < 0)
        {
            size = 0;
        }

        var version = QueryInt(request, "version", 0);
        if (version <= 0)
        {
            version = state.Version;
        }

        var failFirst = QueryInt(request, "failFirst", 0);
        if (failFirst > 0 && state.IncrementFailure() <= failFirst)
        {
            var failStatus = QueryInt(request, "failStatus", state.FailStatus);
            var retryAfter = QueryInt(request, "retryAfter", state.RetryAfterSeconds);

            response.StatusCode = failStatus;
            response.ContentLength = 0;
            if (retryAfter > 0)
            {
                response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            }

            log.Status = failStatus;
            return;
        }

        var etag = BuildETag(state, token: state.Token, version, size);
        if (etag is not null)
        {
            response.Headers.ETag = etag;
        }

        response.Headers.AcceptRanges = "bytes";
        response.Headers.LastModified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .AddDays(version)
            .ToString("R", CultureInfo.InvariantCulture);

        var disposition = Query(request, "cd");
        if (disposition == "unicode")
        {
            response.Headers.ContentDisposition =
                "attachment; filename=\"fallback.bin\"; filename*=UTF-8''%E4%B8%AD%E6%96%87.bin";
        }
        else if (disposition == "ascii")
        {
            response.Headers.ContentDisposition = "attachment; filename=\"hello.bin\"";
        }

        var isHead = HttpMethods.IsHead(request.Method);
        if (isHead && Query(request, "head") == "reject")
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            response.ContentLength = 0;
            return;
        }

        var rangeHeader = request.Headers.Range.ToString();
        var rangeMode = Query(request, "range") ?? "on";

        // "ignore-later" honors the 0-0 probe but answers later Range requests
        // with a full 200, exercising the engine's single-stream fallback.
        if (rangeMode == "ignore-later" &&
            !string.IsNullOrEmpty(rangeHeader) &&
            state.NextRangeRequest() > 1)
        {
            rangeMode = "off";
        }

        var parsedRange = ParseRange(rangeHeader, size);

        // Segment-only failures leave the probe untouched, so tests can inject
        // 429/503 after the engine has already planned parallel segments.
        if (!isHead &&
            !string.IsNullOrEmpty(rangeHeader) &&
            rangeHeader != "bytes=0-0" &&
            state.TryTakeSegmentFailure())
        {
            response.StatusCode = state.FailStatus;
            response.ContentLength = 0;
            if (state.RetryAfterSeconds > 0)
            {
                response.Headers.RetryAfter =
                    state.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            }

            return;
        }

        if (Query(request, "encoding") == "gzip")
        {
            await SendGzipAsync(
                    context,
                    state,
                    size,
                    version,
                    parsedRange?.Start ?? 0,
                    parsedRange?.End ?? Math.Max(0, size - 1),
                    log)
                .ConfigureAwait(false);
            return;
        }

        if (isHead)
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/octet-stream";
            response.ContentLength = size;
            return;
        }

        var throttle = QueryLong(request, "throttle", state.Throttle);
        var stallAfter = QueryLong(request, "stallAfter", -1);
        var stallMs = QueryInt(request, "stallMs", 0);
        var disconnectEvery = QueryLong(request, "disconnectEvery", 0);
        var wrongLengthDelta = QueryInt(request, "wrongLength", 0);
        var slowStart = QueryLong(request, "slowStart", -1);
        var slowDelayMs = QueryInt(request, "slowDelayMs", 0);

        long start;
        long bodyLength;

        if (rangeMode != "off" && parsedRange is { } range)
        {
            start = range.Start;
            var end = range.End;

            if (start >= size || end < start)
            {
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = $"bytes */{size}";
                response.ContentLength = 0;
                return;
            }

            if (rangeMode == "bad-start")
            {
                start = Math.Max(0, start - 1);
            }

            var requestedLength = end - start + 1;
            bodyLength = rangeMode == "short" ? requestedLength / 2 : requestedLength;

            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = $"bytes {start}-{end}/{size}";
            response.ContentType = "application/octet-stream";
            response.ContentLength = Math.Max(0, bodyLength + wrongLengthDelta);
        }
        else
        {
            start = 0;
            bodyLength = size;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/octet-stream";
            response.ContentLength = Math.Max(0, size + wrongLengthDelta);
        }

        await SendBodyAsync(
                context,
                state,
                size,
                version,
                start,
                bodyLength,
                throttle,
                stallAfter,
                stallMs,
                disconnectEvery,
                wrongLengthDelta,
                slowStart,
                slowDelayMs,
                log)
            .ConfigureAwait(false);
    }

    private static async Task SendBodyAsync(
        HttpContext context,
        TokenState state,
        long size,
        int version,
        long start,
        long length,
        long throttle,
        long stallAfter,
        int stallMs,
        long disconnectEvery,
        int wrongLengthDelta,
        long slowStart,
        int slowDelayMs,
        RequestLog log)
    {
        var seed = DeterministicContent.SeedFor(state.Token, size, version);
        var generator = new ContentStream(seed, size, start);
        var buffer = new byte[64 * 1024];
        var stopwatch = Stopwatch.StartNew();
        var sent = 0L;
        var stalled = false;

        while (sent < length)
        {
            var chunk = (int)Math.Min(buffer.Length, length - sent);
            var count = generator.Read(buffer.AsSpan(0, chunk));
            if (count <= 0)
            {
                break;
            }

            if (stallAfter >= 0 && sent >= stallAfter && !stalled && stallMs > 0)
            {
                stalled = true;
                await Task.Delay(stallMs, context.RequestAborted).ConfigureAwait(false);
            }

            await context.Response.Body
                .WriteAsync(buffer.AsMemory(0, count), context.RequestAborted)
                .ConfigureAwait(false);
            await context.Response.Body
                .FlushAsync(context.RequestAborted)
                .ConfigureAwait(false);

            sent += count;
            log.BytesSent = sent;
            state.AddBytesServed(count);

            // Pauses one range after it has begun streaming, so tests can make
            // a single segment straggle without holding up the probe.
            if (slowDelayMs > 0 &&
                slowStart >= 0 &&
                start == slowStart &&
                length > 1 &&
                sent == count)
            {
                await Task.Delay(slowDelayMs, context.RequestAborted).ConfigureAwait(false);
            }

            if (disconnectEvery > 0 && sent >= disconnectEvery)
            {
                await context.Response.Body
                    .FlushAsync(context.RequestAborted)
                    .ConfigureAwait(false);
                context.Abort();
                return;
            }

            if (throttle > 0)
            {
                var expectedMs = sent * 1000L / throttle;
                var delayMs = expectedMs - stopwatch.ElapsedMilliseconds;
                if (delayMs > 0)
                {
                    await Task.Delay(
                            (int)Math.Min(delayMs, int.MaxValue),
                            context.RequestAborted)
                        .ConfigureAwait(false);
                }
            }
        }

        if (wrongLengthDelta > 0)
        {
            await context.Response.Body
                .FlushAsync(context.RequestAborted)
                .ConfigureAwait(false);
            context.Abort();
        }
    }

    private static async Task SendGzipAsync(
        HttpContext context,
        TokenState state,
        long size,
        int version,
        long start,
        long end,
        RequestLog log)
    {
        var length = end >= start ? end - start + 1 : 0;
        if (length > 64L * 1024 * 1024)
        {
            throw new InvalidOperationException("gzip test payload is too large.");
        }

        var raw = new byte[(int)length];
        var seed = DeterministicContent.SeedFor(state.Token, size, version);
        new ContentStream(seed, size, start).Read(raw);

        using var memory = new MemoryStream();
        await using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(raw, context.RequestAborted).ConfigureAwait(false);
        }

        var compressed = memory.ToArray();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers.ContentEncoding = "gzip";
        context.Response.ContentLength = compressed.Length;

        log.Status = StatusCodes.Status200OK;
        log.BytesSent = compressed.Length;
        state.AddBytesServed(compressed.Length);

        await context.Response.Body
            .WriteAsync(compressed, context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static IResult HandleChecksum(HttpContext context, TokenStore store)
    {
        var token = Query(context.Request, "token") ?? "default";
        var state = store.Get(token);
        var size = QueryLong(context.Request, "size", DefaultSize);
        var version = QueryInt(context.Request, "version", 0);
        if (version <= 0)
        {
            version = state.Version;
        }

        var checksum = store.GetChecksum(token, size, version);
        return Results.Json(new { length = size, sha256 = checksum });
    }

    private static IResult HandleLog(HttpContext context, TokenStore store)
    {
        var state = store.Get(Query(context.Request, "token") ?? "default");
        return Results.Json(state.Logs.OrderBy(entry => entry.Index).ToArray());
    }

    private static IResult HandleStats(HttpContext context, TokenStore store)
    {
        var state = store.Get(Query(context.Request, "token") ?? "default");
        return Results.Json(new
        {
            requests = state.Logs.Count,
            maxConcurrent = state.MaxConcurrent,
            bytesServed = state.BytesServed,
        });
    }

    private static IResult HandleConfig(HttpContext context, TokenStore store)
    {
        var state = store.Get(Query(context.Request, "token") ?? "default");

        var version = QueryInt(context.Request, "version", 0);
        if (version > 0)
        {
            state.Version = version;
        }

        var etagMode = Query(context.Request, "etag");
        if (!string.IsNullOrEmpty(etagMode))
        {
            state.ETagMode = etagMode;
        }

        var throttle = QueryLong(context.Request, "throttle", -1);
        if (throttle >= 0)
        {
            state.Throttle = throttle;
        }

        var failFirst = QueryInt(context.Request, "failFirst", -1);
        if (failFirst >= 0)
        {
            state.FailFirst = failFirst;
        }

        var retryAfter = QueryInt(context.Request, "retryAfter", -1);
        if (retryAfter >= 0)
        {
            state.RetryAfterSeconds = retryAfter;
        }

        var failStatus = QueryInt(context.Request, "failStatus", -1);
        if (failStatus > 0)
        {
            state.FailStatus = failStatus;
        }

        var failSegments = QueryInt(context.Request, "failSegments", -1);
        if (failSegments >= 0)
        {
            state.FailSegments = failSegments;
        }

        return Results.Json(new
        {
            ok = true,
            version = state.Version,
            etagMode = state.ETagMode,
            throttle = state.Throttle,
            failSegments = state.FailSegments,
        });
    }

    private static IResult HandleReset(HttpContext context, TokenStore store)
    {
        var state = store.Get(Query(context.Request, "token") ?? "default");
        state.Reset();
        return Results.Json(new { ok = true });
    }

    private static async Task HandleChunkedAsync(HttpContext context)
    {
        const int total = 64 * 1024;
        var buffer = new byte[8 * 1024];

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/octet-stream";

        // Deliberately no Content-Length: Kestrel answers with chunked
        // transfer encoding, which the engine cannot size or resume.
        for (var sent = 0; sent < total; sent += buffer.Length)
        {
            for (var index = 0; index < buffer.Length; index++)
            {
                buffer[index] = (byte)((sent + index) % 251);
            }

            await context.Response.Body
                .WriteAsync(buffer, context.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    private static IResult HandleCookieStart(HttpContext context)
    {
        context.Response.Cookies.Append(
            "session",
            "ok",
            new CookieOptions { Path = "/", HttpOnly = true });

        var query = context.Request.QueryString.Value ?? string.Empty;
        return Results.Redirect("/dl" + query);
    }

    private static string? BuildETag(TokenState state, string token, int version, long size) =>
        state.ETagMode switch
        {
            "none" => null,
            "weak" => $"W/\"{token}-{version}-{size}\"",
            "random" => $"\"{Guid.NewGuid():N}\"",
            _ => $"\"{token}-{version}-{size}\"",
        };

    private static (long Start, long End)? ParseRange(string? header, long size)
    {
        if (string.IsNullOrWhiteSpace(header) || size <= 0 ||
            !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var spec = header[6..].Trim();
        var comma = spec.IndexOf(',');
        if (comma >= 0)
        {
            spec = spec[..comma].Trim();
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();

        long start;
        long end;

        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) ||
                suffix <= 0)
            {
                return null;
            }

            start = Math.Max(0, size - suffix);
            end = size - 1;
        }
        else
        {
            if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
                start < 0)
            {
                return null;
            }

            if (endText.Length == 0)
            {
                end = size - 1;
            }
            else if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out end) ||
                     end < start)
            {
                return null;
            }
        }

        if (start >= size)
        {
            return (start, end);
        }

        if (end >= size)
        {
            end = size - 1;
        }

        return (start, end);
    }

    private static string? Query(HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var value) ? value.ToString() : null;

    private static int QueryInt(HttpRequest request, string name, int fallback) =>
        int.TryParse(Query(request, name), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    private static long QueryLong(HttpRequest request, string name, long fallback) =>
        long.TryParse(Query(request, name), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;
}
