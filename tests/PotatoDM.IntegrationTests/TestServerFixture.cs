using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using PotatoDM.TestServer;

namespace PotatoDM.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class TestServerCollection : ICollectionFixture<TestServerFixture>
{
    public const string Name = "TestServer";
}

public sealed class TestServerFixture : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private WebApplication? _app;
    private HttpClient? _http;

    public string BaseAddress { get; private set; } = string.Empty;

    public HttpClient Http =>
        _http ?? throw new InvalidOperationException("The test server is not running.");

    public async Task InitializeAsync()
    {
        _app = TestServerApp.Build(port: 0);
        await _app.StartAsync();
        BaseAddress = TestServerApp.GetBoundAddress(_app).TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseAddress),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    public string Url(string pathAndQuery) => BaseAddress + pathAndQuery;

    public async Task<string> GetChecksumAsync(string token, long size, int version = 0)
    {
        var query = $"/checksum?token={Uri.EscapeDataString(token)}&size={size}";
        if (version > 0)
        {
            query += $"&version={version}";
        }

        using var document = JsonDocument.Parse(await Http.GetStringAsync(query));
        return document.RootElement.GetProperty("sha256").GetString()!;
    }

    public async Task<List<RequestLogDto>> GetLogAsync(string token)
    {
        var json = await Http.GetStringAsync($"/admin/log?token={Uri.EscapeDataString(token)}");
        return JsonSerializer.Deserialize<List<RequestLogDto>>(json, JsonOptions) ?? new List<RequestLogDto>();
    }

    public async Task<StatsDto> GetStatsAsync(string token)
    {
        var json = await Http.GetStringAsync($"/admin/stats?token={Uri.EscapeDataString(token)}");
        return JsonSerializer.Deserialize<StatsDto>(json, JsonOptions) ?? new StatsDto();
    }

    public async Task ConfigureAsync(
        string token,
        int? version = null,
        int? failSegments = null,
        int? failStatus = null,
        int? retryAfter = null)
    {
        var parameters = new List<string> { "token=" + Uri.EscapeDataString(token) };

        if (version is { } value)
        {
            parameters.Add("version=" + value);
        }

        if (failSegments is { } segments)
        {
            parameters.Add("failSegments=" + segments);
        }

        if (failStatus is { } status)
        {
            parameters.Add("failStatus=" + status);
        }

        if (retryAfter is { } seconds)
        {
            parameters.Add("retryAfter=" + seconds);
        }

        using var response = await Http.GetAsync("/admin/config?" + string.Join("&", parameters));
        response.EnsureSuccessStatusCode();
    }
}

public sealed class RequestLogDto
{
    public int Index { get; set; }

    public string Method { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string? Range { get; set; }

    public string? IfRange { get; set; }

    public string? Cookie { get; set; }

    public int Status { get; set; }

    public long BytesSent { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? EndedUtc { get; set; }
}

public sealed class StatsDto
{
    public int Requests { get; set; }

    public int MaxConcurrent { get; set; }

    public long BytesServed { get; set; }
}

internal static class IntegrationHelpers
{
    public static string NewToken() => "it-" + Guid.NewGuid().ToString("N");

    public static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static (long Start, long End)? ParseRangeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var spec = header[6..].Trim();
        var dash = spec.IndexOf('-');
        if (dash <= 0)
        {
            return null;
        }

        if (!long.TryParse(spec[..dash], out var start) ||
            !long.TryParse(spec[(dash + 1)..], out var end))
        {
            return null;
        }

        return (start, end);
    }

    public static bool IsProbe(RequestLogDto entry) =>
        entry.Method == "GET" && entry.Range == "bytes=0-0";

    public static bool IsSegment(RequestLogDto entry) =>
        entry.Method == "GET" &&
        entry.Range is not null &&
        entry.Range != "bytes=0-0";

    public static int PeakConcurrency(IEnumerable<RequestLogDto> entries)
    {
        var events = new List<(DateTimeOffset Time, int Delta)>();

        foreach (var entry in entries)
        {
            events.Add((entry.StartedUtc, 1));
            events.Add((entry.EndedUtc ?? DateTimeOffset.MaxValue, -1));
        }

        // A request that ends at the same instant another starts does not overlap.
        events.Sort((left, right) => left.Time == right.Time
            ? left.Delta.CompareTo(right.Delta)
            : left.Time.CompareTo(right.Time));

        var current = 0;
        var peak = 0;

        foreach (var (_, delta) in events)
        {
            current += delta;
            peak = Math.Max(peak, current);
        }

        return peak;
    }

    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    public static async Task<long> BytesRecordedInStateAsync(string statePath)
    {
        try
        {
            var text = await File.ReadAllTextAsync(statePath);
            using var document = JsonDocument.Parse(text);

            if (!document.RootElement.TryGetProperty("segments", out var segments))
            {
                return 0;
            }

            return segments
                .EnumerateArray()
                .Sum(segment => segment.GetProperty("bytesDone").GetInt64());
        }
        catch (IOException)
        {
            return 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public static CancellationToken Timeout(int seconds) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}
