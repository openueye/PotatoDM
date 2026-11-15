using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Media;

namespace PotatoDM.Core.Tests;

/// <summary>
/// The media path shares the CLI exit-code contract with the direct-download
/// path. A transport error that survives every retry must be reported as a
/// transient failure (9), not as an unclassified error (1).
/// </summary>
public class MediaHttpRetryTests
{
    private static readonly Uri ManifestUri = new("https://example.test/stream.m3u8");

    [Fact]
    public void A_client_timeout_is_retryable_when_the_caller_did_not_cancel()
    {
        Assert.True(MediaHttp.IsRetryable(
            new TaskCanceledException("the request timed out"),
            CancellationToken.None));
    }

    [Fact]
    public void User_cancellation_is_not_retried()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.False(MediaHttp.IsRetryable(
            new TaskCanceledException("canceled by caller"),
            canceled.Token));
    }

    [Fact]
    public void An_exhausted_transport_error_becomes_a_transient_failure()
    {
        var wrapped = MediaHttp.RetryExhausted(
            new HttpRequestException("connection reset"),
            ManifestUri,
            attempts: 4);

        Assert.True(wrapped.IsTransient);
        Assert.Equal(DownloadExitCode.TransientFailure, wrapped.ExitCode);
    }

    [Fact]
    public void An_exhausted_http_classification_is_preserved()
    {
        var original = new HttpDownloadException("503 after retries", isTransient: true);

        var wrapped = MediaHttp.RetryExhausted(original, ManifestUri, attempts: 4);

        Assert.Same(original, wrapped);
    }
}
