using System.Net;

namespace PotatoDM.Core.Transfer;

public sealed class RetryPolicy
{
    private readonly Func<double> _jitter;

    public RetryPolicy(int maxRetries = 5, TimeSpan? maxDelay = null, Func<double>? jitter = null)
    {
        MaxRetries = Math.Max(0, maxRetries);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
    }

    public int MaxRetries { get; }

    public TimeSpan MaxDelay { get; }

    public static bool IsRetryableStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is 408 or 429 or (>= 500 and <= 599);
    }

    /// <summary>
    /// Retry-After wins when present. Otherwise exponential backoff with
    /// +/-20% jitter, capped by <see cref="MaxDelay"/>.
    /// </summary>
    public TimeSpan GetDelay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } requested)
        {
            return requested <= TimeSpan.Zero
                ? TimeSpan.Zero
                : requested > MaxDelay ? MaxDelay : requested;
        }

        var exponent = Math.Clamp(attempt, 0, 10);
        var baseMs = Math.Min(MaxDelay.TotalMilliseconds, 250.0 * Math.Pow(2, exponent));
        var jitter = 0.8 + (0.4 * Math.Clamp(_jitter(), 0.0, 1.0));
        return TimeSpan.FromMilliseconds(Math.Min(MaxDelay.TotalMilliseconds, baseMs * jitter));
    }
}
