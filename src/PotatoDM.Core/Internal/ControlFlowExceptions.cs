using System.Net;

namespace PotatoDM.Core.Internal;

/// <summary>The server answered a Range request with 200. The job must fall back to a single stream.</summary>
internal sealed class RangeIgnoredException : Exception
{
    public RangeIgnoredException()
        : base("Server ignored the Range header and returned 200.")
    {
    }
}

/// <summary>No bytes arrived within the configured stall timeout.</summary>
internal sealed class StallTimeoutException : Exception
{
    public StallTimeoutException(TimeSpan timeout)
        : base($"No data received for {timeout.TotalSeconds:0.#} seconds.")
    {
    }
}

/// <summary>A response whose status or byte range violates the protocol contract; retried.</summary>
internal sealed class UnexpectedRangeResponseException : Exception
{
    public UnexpectedRangeResponseException(string message)
        : base(message)
    {
    }
}

/// <summary>A retryable HTTP status with an optional Retry-After hint.</summary>
internal sealed class RetryableHttpException : Exception
{
    public RetryableHttpException(HttpStatusCode statusCode, TimeSpan? retryAfter)
        : base($"Transient HTTP status {(int)statusCode} ({statusCode}).")
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}
