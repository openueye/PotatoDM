using System.Net;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Internal;
using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Tests;

/// <summary>
/// Pins how a failure that survived every retry is reported to the caller.
/// </summary>
/// <remarks>
/// The asymmetry is deliberate: a false "transient" costs a few extra attempts,
/// while a false "permanent" makes a script abandon a download that would have
/// succeeded. Only a contradiction the server will repeat earns the permanent code.
/// </remarks>
public class RetryExhaustionTests
{
    public static TheoryData<Exception?> TransientFailures => new()
    {
        new StallTimeoutException(TimeSpan.FromSeconds(5)),
        new RetryableHttpException(HttpStatusCode.ServiceUnavailable, null),
        new HttpRequestException("connection reset"),
        new IOException("connection aborted"),
        new TaskCanceledException("the request timed out"),
        new UnexpectedRangeResponseException("short read", isTransient: true),
        null,
    };

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public void A_failure_that_can_clear_on_its_own_is_reported_as_transient(Exception? error)
    {
        Assert.True(SegmentWorker.IsTransientRetryExhaustion(error));
    }

    [Fact]
    public void A_deterministic_protocol_violation_is_reported_as_permanent()
    {
        var error = new UnexpectedRangeResponseException(
            "a 416 while bytes remain", isTransient: false);

        Assert.False(SegmentWorker.IsTransientRetryExhaustion(error));
    }

    [Theory]
    [InlineData(true, DownloadExitCode.TransientFailure)]
    [InlineData(false, DownloadExitCode.HttpPermanentFailure)]
    public void The_classification_drives_the_exit_code(bool isTransient, int expectedCode)
    {
        var failure = new UnexpectedRangeResponseException("range mismatch", isTransient);

        var wrapped = new HttpDownloadException(
            "Segment 0 failed after 1 attempts.",
            SegmentWorker.IsTransientRetryExhaustion(failure),
            failure);

        Assert.Equal(expectedCode, wrapped.ExitCode);
        Assert.Equal(isTransient, wrapped.IsTransient);
    }
}
