using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Models;

namespace PotatoDM.Core.Transfer;

/// <summary>Validates requests and persisted segment maps before starting workers.</summary>
internal static class DownloadValidation
{
    internal static long RequireKnownLength(ProbeResult probe)
    {
        if (probe.TotalLength is not { } total || total < 0)
        {
            throw new ProbeException(
                "The server did not provide Content-Length; a known resource length is required.");
        }

        return total;
    }

    internal static bool ValidatorsMatch(JobState state, ProbeResult probe)
    {
        if (probe.TotalLength is { } total && total != state.TotalLength)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(state.ETag) && !string.IsNullOrEmpty(probe.ETag))
        {
            return string.Equals(state.ETag, probe.ETag, StringComparison.Ordinal);
        }

        if (state.LastModified is not null && probe.LastModified is { } lastModified)
        {
            return string.Equals(
                state.LastModified,
                lastModified.ToString("O"),
                StringComparison.Ordinal);
        }

        // No comparable validator on both sides; the length check above is the best available test.
        return true;
    }

    internal static void ValidateSegments(JobState state)
    {
        if (state.TotalLength < 0)
        {
            throw new StateException("State contains a negative total length.");
        }

        if (state.Segments.Count == 0)
        {
            throw new StateException("State contains no segments.");
        }

        var expectedStart = 0L;

        // Dynamic splits append helper ranges with higher indices; offset order
        // is what defines coverage.
        foreach (var segment in state.Segments.OrderBy(s => s.Start))
        {
            if (segment.Start != expectedStart ||
                segment.EndExclusive < segment.Start ||
                segment.BytesDone < 0 ||
                segment.BytesDone > segment.Length)
            {
                throw new StateException("State contains an invalid segment map.");
            }

            expectedStart = segment.EndExclusive;
        }

        if (expectedStart != state.TotalLength)
        {
            throw new StateException(
                $"Segment map covers {expectedStart} bytes but the state length is {state.TotalLength}.");
        }
    }

    internal static void ValidateRequest(DownloadRequest request)
    {
        if (request.Url is null)
        {
            throw new ArgumentException("A URL is required.", nameof(request));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(request.Connections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MinSegmentSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxConnectionsPerHost, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxConnectionsPerJob, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(request.MaxRetries);

        if (request.StallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), "StallTimeout must be positive.");
        }
    }

    internal static void ValidateResumeOptions(ResumeOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConnectionsPerHost, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConnectionsPerJob, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetries);

        if (options.StallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "StallTimeout must be positive.");
        }
    }

    internal static void EnsureDiskSpace(string targetPath, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
            {
                throw new DiskSpaceException(
                    $"Not enough free space on '{drive.Name}': need {requiredBytes:N0} bytes, " +
                    $"{drive.AvailableFreeSpace:N0} available.");
            }
        }
        catch (ArgumentException)
        {
            // Unusual path form; preallocation will report the real problem.
        }
    }
}
