namespace PotatoDM.Core.Exceptions;

/// <summary>CLI exit codes shared by the engine and the console client.</summary>
public static class DownloadExitCode
{
    public const int Success = 0;
    public const int Unexpected = 1;
    public const int BadArguments = 2;
    public const int ProbeFailed = 3;
    public const int HttpPermanentFailure = 4;
    public const int ValidatorMismatch = 5;
    public const int FileAccess = 6;
    public const int StateInvalid = 7;

    /// <summary>DRM-protected or otherwise unsupported media was refused.</summary>
    public const int UnsupportedMedia = 8;

    public const int Canceled = 130;
}

/// <summary>Base class for errors that map to a stable CLI exit code.</summary>
public class PotatoDownloadException : Exception
{
    public PotatoDownloadException(string message, int exitCode, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}

public sealed class ProbeException : PotatoDownloadException
{
    public ProbeException(string message, Exception? innerException = null)
        : base(message, DownloadExitCode.ProbeFailed, innerException)
    {
    }
}

public sealed class HttpDownloadException : PotatoDownloadException
{
    public HttpDownloadException(string message, bool isTransient, Exception? innerException = null)
        : base(message, DownloadExitCode.HttpPermanentFailure, innerException)
    {
        IsTransient = isTransient;
    }

    public bool IsTransient { get; }
}

public sealed class ValidatorMismatchException : PotatoDownloadException
{
    public ValidatorMismatchException(string message)
        : base(message, DownloadExitCode.ValidatorMismatch)
    {
    }
}

public sealed class UnsupportedContentEncodingException : PotatoDownloadException
{
    public UnsupportedContentEncodingException(string encoding)
        : base($"Server returned Content-Encoding '{encoding}' despite a request for identity encoding.",
            DownloadExitCode.HttpPermanentFailure)
    {
    }
}

public sealed class DiskSpaceException : PotatoDownloadException
{
    public DiskSpaceException(string message, Exception? innerException = null)
        : base(message, DownloadExitCode.FileAccess, innerException)
    {
    }
}

public sealed class FileAccessException : PotatoDownloadException
{
    public FileAccessException(string message, Exception? innerException = null)
        : base(message, DownloadExitCode.FileAccess, innerException)
    {
    }
}

public sealed class StateException : PotatoDownloadException
{
    public StateException(string message, Exception? innerException = null)
        : base(message, DownloadExitCode.StateInvalid, innerException)
    {
    }
}

public sealed class JobAlreadyRunningException : PotatoDownloadException
{
    public JobAlreadyRunningException(string message, Exception? innerException = null)
        : base(message, DownloadExitCode.StateInvalid, innerException)
    {
    }
}

/// <summary>A manifest is malformed, live, or uses a feature that cannot be merged.</summary>
public sealed class MediaManifestException : PotatoDownloadException
{
    public MediaManifestException(string message, Exception? innerException = null)
        : base(message, DownloadExitCode.ProbeFailed, innerException)
    {
    }
}

/// <summary>
/// The media is protected by a DRM system. This client deliberately does not
/// implement any decryption of DRM-protected content.
/// </summary>
public sealed class DrmProtectedException : PotatoDownloadException
{
    public DrmProtectedException(string system)
        : base(
            $"This media is protected by {system} DRM. PotatoDM identifies DRM and refuses " +
            "to download it; it does not bypass content protection.",
            DownloadExitCode.UnsupportedMedia)
    {
        System = system;
    }

    public string System { get; }
}

/// <summary>Merging separate audio and video tracks needs an external muxer.</summary>
public sealed class MediaMergeException : PotatoDownloadException
{
    public MediaMergeException(string message, Exception? innerException = null)
        : base(message, DownloadExitCode.FileAccess, innerException)
    {
    }
}
