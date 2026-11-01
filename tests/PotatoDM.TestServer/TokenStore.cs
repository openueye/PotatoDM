using System.Collections.Concurrent;

namespace PotatoDM.TestServer;

public sealed class TokenStore
{
    private readonly ConcurrentDictionary<string, TokenState> _states =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<(string Token, long Size, int Version), string> _checksums = new();

    public TokenState Get(string token) =>
        _states.GetOrAdd(token, static value => new TokenState(value));

    public string GetChecksum(string token, long size, int version)
    {
        return _checksums.GetOrAdd(
            (token, size, version),
            static key =>
            {
                var seed = DeterministicContent.SeedFor(key.Token, key.Size, key.Version);
                return DeterministicContent.ComputeSha256(seed, key.Size);
            });
    }
}

public sealed class TokenState
{
    private int _version = 1;
    private long _throttle;
    private int _failCount;
    private int _activeRequests;
    private int _maxConcurrent;
    private long _bytesServed;
    private int _requestIndex;
    private int _rangeRequests;
    private int _segmentFailCount;
    private int _failSegments;

    public TokenState(string token)
    {
        Token = token;
    }

    public string Token { get; }

    public volatile string ETagMode = "strong";

    public int FailFirst { get; set; }

    public int FailStatus { get; set; } = 503;

    public int RetryAfterSeconds { get; set; }

    /// <summary>Number of *segment* requests (not the 0-0 probe) to fail.</summary>
    public int FailSegments
    {
        get => Volatile.Read(ref _failSegments);
        set => Volatile.Write(ref _failSegments, Math.Max(0, value));
    }

    public int NextRangeRequest() => Interlocked.Increment(ref _rangeRequests);

    public bool TryTakeSegmentFailure()
    {
        var limit = Volatile.Read(ref _failSegments);
        return limit > 0 && Interlocked.Increment(ref _segmentFailCount) <= limit;
    }

    public int Version
    {
        get => Volatile.Read(ref _version);
        set => Volatile.Write(ref _version, Math.Max(1, value));
    }

    public long Throttle
    {
        get => Interlocked.Read(ref _throttle);
        set => Interlocked.Exchange(ref _throttle, Math.Max(0, value));
    }

    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    public long BytesServed => Interlocked.Read(ref _bytesServed);

    public ConcurrentQueue<RequestLog> Logs { get; } = new();

    public int IncrementFailure() => Interlocked.Increment(ref _failCount);

    public int NextRequestIndex() => Interlocked.Increment(ref _requestIndex);

    public void AddBytesServed(long count) => Interlocked.Add(ref _bytesServed, count);

    public void Enter()
    {
        var active = Interlocked.Increment(ref _activeRequests);

        while (true)
        {
            var current = Volatile.Read(ref _maxConcurrent);
            if (active <= current ||
                Interlocked.CompareExchange(ref _maxConcurrent, active, current) == current)
            {
                return;
            }
        }
    }

    public void Exit() => Interlocked.Decrement(ref _activeRequests);

    public void Reset()
    {
        while (Logs.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _bytesServed, 0);
        Interlocked.Exchange(ref _maxConcurrent, 0);
        Interlocked.Exchange(ref _failCount, 0);
        Interlocked.Exchange(ref _requestIndex, 0);
        Interlocked.Exchange(ref _rangeRequests, 0);
        Interlocked.Exchange(ref _segmentFailCount, 0);
        Interlocked.Exchange(ref _failSegments, 0);
    }
}

public sealed class RequestLog
{
    public int Index { get; set; }

    public string Method { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string? Range { get; set; }

    public string? IfRange { get; set; }

    public string? Cookie { get; set; }

    public int Status { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? EndedUtc { get; set; }

    public long BytesSent { get; set; }
}
