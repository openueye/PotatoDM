using System.Net;
using PotatoDM.Core.Exceptions;

namespace PotatoDM.AppCore.Job;

public enum JobFailureKind { Other, Authentication, Storage, MediaTool, UnsupportedMedia }

public static class JobFailure
{
    public static JobFailureKind Classify(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var status = (current as HttpRequestException)?.StatusCode;
            if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden ||
                current.Message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
                return JobFailureKind.Authentication;
        }
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is DrmProtectedException or MediaManifestException or UnsupportedContentEncodingException)
                return JobFailureKind.UnsupportedMedia;
            if (current is MediaMergeException) return JobFailureKind.MediaTool;
            if (current is DiskSpaceException or FileAccessException or UnauthorizedAccessException)
                return JobFailureKind.Storage;
        }
        return JobFailureKind.Other;
    }

    public static string Details(Exception error)
    {
        var cause = error.GetBaseException().Message;
        return string.Equals(error.Message, cause, StringComparison.Ordinal)
            ? error.Message : error.Message + "\n" + cause;
    }
}
