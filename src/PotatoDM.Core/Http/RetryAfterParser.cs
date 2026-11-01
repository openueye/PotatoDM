namespace PotatoDM.Core.Http;

/// <summary>Reads the delay advertised by a <c>Retry-After</c> response header.</summary>
internal static class RetryAfterParser
{
    public static TimeSpan? Parse(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}
