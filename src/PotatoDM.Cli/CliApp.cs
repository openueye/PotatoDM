using System.Globalization;
using System.Text.Json;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Formatting;
using PotatoDM.Core.Models;
using PotatoDM.Core.Transfer;
using PotatoDM.Torrent;

namespace PotatoDM.Cli;

public static class CliApp
{
    private const string Version = "0.1.0";

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(string[] args)
    {
        ParsedArguments parsed;
        try
        {
            parsed = ParsedArguments.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            PrintUsage();
            return 2;
        }

        var json = parsed.GetBool("json");
        var quiet = parsed.GetBool("quiet");
        if (parsed.GetBool("help") || parsed.Positionals.Count == 0)
        {
            PrintUsage();
            return parsed.Positionals.Count == 0 ? 2 : 0;
        }

        var command = parsed.Positionals[0].ToLowerInvariant();
        using var cancellation = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            return command switch
            {
                "probe" => await RunProbeAsync(parsed, json, cancellation.Token),
                "download" => await RunDownloadAsync(parsed, json, quiet, cancellation.Token),
                "resume" => await RunResumeAsync(parsed, json, quiet, cancellation.Token),
                "torrent" => await RunTorrentAsync(parsed, json, quiet, cancellation.Token),
                _ => Fail("unknown command '" + command + "'", 2, json),
            };
        }
        catch (Exception ex)
        {
            var exitCode = MapExitCode(ex);
            if (json)
            {
                WriteEvent(new
                {
                    type = "failed",
                    error = ex.Message,
                    exitCode,
                    canceled = ex is OperationCanceledException,
                });
            }
            else
            {
                Console.Error.WriteLine("error: " + ex.Message);
            }

            return exitCode;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            if (!json && !quiet)
            {
                Console.Out.WriteLine();
            }
        }
    }

    private static async Task<int> RunProbeAsync(ParsedArguments parsed, bool json, CancellationToken cancellationToken)
    {
        Uri url = RequireUrl(parsed);
        DownloadEngine engine = new DownloadEngine();
        ProbeResult probe = await engine.ProbeAsync(url, BuildRequestOptions(parsed), cancellationToken);
        if (json)
        {
            WriteEvent(new
            {
                type = "probe",
                url = probe.RequestedUri.ToString(),
                finalUrl = probe.FinalUri.ToString(),
                totalLength = probe.TotalLength,
                supportsRanges = probe.SupportsRanges,
                etag = probe.ETag,
                lastModified = probe.LastModified,
                suggestedFileName = probe.SuggestedFileName,
                contentType = probe.ContentType
            });
        }
        else
        {
            Console.Out.WriteLine($"URL          : {probe.RequestedUri}");
            Console.Out.WriteLine($"Final URL    : {probe.FinalUri}");
            Console.Out.WriteLine("Length       : " + (probe.TotalLength?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown"));
            Console.Out.WriteLine("Ranges       : " + (probe.SupportsRanges ? "supported" : "not supported"));
            Console.Out.WriteLine("ETag         : " + (probe.ETag ?? "-"));
            Console.Out.WriteLine("Last-Modified: " + (probe.LastModified?.ToString("O") ?? "-"));
            Console.Out.WriteLine("File name    : " + (probe.SuggestedFileName ?? "-"));
            Console.Out.WriteLine("Content-Type : " + (probe.ContentType ?? "-"));
        }
        return 0;
    }

    private static async Task<int> RunDownloadAsync(ParsedArguments parsed, bool json, bool quiet, CancellationToken cancellationToken)
    {
        Uri url = RequireUrl(parsed);
        DownloadRequest request = new DownloadRequest
        {
            Url = url,
            TargetPath = parsed.Get("output"),
            Connections = parsed.GetInt("connections", 8),
            MinSegmentSize = parsed.GetLong("min-segment-size", 1048576L),
            StatePath = parsed.Get("state"),
            Restart = parsed.GetBool("restart"),
            RestartOnChange = parsed.GetBool("restart-on-change"),
            DynamicSegmentation = parsed.GetBool("dynamic-segments"),
            SparseWorkingFile = parsed.GetBool("sparse"),
            FfmpegPath = parsed.Get("ffmpeg"),
            StallTimeout = TimeSpan.FromSeconds(parsed.GetInt("stall-timeout", 30)),
            MaxRetries = parsed.GetInt("max-retries", 5),
            MaxRetryDelay = TimeSpan.FromSeconds(parsed.GetInt("max-retry-delay", 30)),
            MaxConnectionsPerHost = parsed.GetInt("max-per-host", 8),
            MaxConnectionsPerJob = PerJobConnections(parsed),
            BandwidthLimiter = CreateBandwidthLimiter(parsed),
            RequestOptions = BuildRequestOptions(parsed)
        };
        DownloadEngine engine = new DownloadEngine();
        AttachProgress(engine, json, quiet);
        DownloadResult result = await engine.DownloadAsync(request, cancellationToken);
        ReportCompleted(result, json, quiet);
        return 0;
    }

    private static async Task<int> RunResumeAsync(ParsedArguments parsed, bool json, bool quiet, CancellationToken cancellationToken)
    {
        var statePath = parsed.Positionals.Count > 1 ? parsed.Positionals[1] : null;
        if (string.IsNullOrWhiteSpace(statePath))
        {
            return Fail("resume requires a state or working file path", 2, json);
        }
        DownloadEngine engine = new DownloadEngine();
        AttachProgress(engine, json, quiet);
        ResumeOptions options = new ResumeOptions
        {
            RestartOnChange = parsed.GetBool("restart-on-change"),
            DynamicSegmentation = parsed.GetBool("dynamic-segments"),
            SparseWorkingFile = parsed.GetBool("sparse"),
            StallTimeout = TimeSpan.FromSeconds(parsed.GetInt("stall-timeout", 30)),
            MaxRetries = parsed.GetInt("max-retries", 5),
            MaxRetryDelay = TimeSpan.FromSeconds(parsed.GetInt("max-retry-delay", 30)),
            MaxConnectionsPerHost = parsed.GetInt("max-per-host", 8),
            MaxConnectionsPerJob = PerJobConnections(parsed),
            BandwidthLimiter = CreateBandwidthLimiter(parsed),
            RequestOptions = BuildRequestOptions(parsed)
        };
        DownloadResult result = await engine.ResumeAsync(statePath, options, cancellationToken);
        ReportCompleted(result, json, quiet);
        return 0;
    }

    private static void ReportCompleted(DownloadResult result, bool json, bool quiet)
    {
        if (json)
        {
            WriteEvent(new
            {
                type = "completed",
                path = result.TargetPath,
                bytes = result.TotalBytes,
                mode = result.Mode.ToString().ToLowerInvariant(),
                durationSeconds = result.Duration.TotalSeconds
            });
        }
        else if (!quiet)
        {
            Console.Out.WriteLine($"saved {result.TotalBytes:N0} bytes to {result.TargetPath} ({result.Mode}, {result.Duration.TotalSeconds:0.0}s)");
        }
    }

    private static async Task<int> RunTorrentAsync(ParsedArguments parsed, bool json, bool quiet, CancellationToken cancellationToken)
    {
        if (parsed.Positionals.Count < 2)
        {
            return Fail("torrent requires a magnet link or .torrent path", 2, json);
        }
        string source = parsed.Positionals[1];
        if (!source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) && !File.Exists(source))
        {
            return Fail("torrent source not found: " + source, 2, json);
        }
        string directory = Path.GetFullPath(parsed.Get("output") ?? Directory.GetCurrentDirectory());
        Directory.CreateDirectory(directory);
        TorrentDownloadRequest request = new TorrentDownloadRequest
        {
            Source = source,
            DownloadDirectory = directory,
            ListenPort = parsed.GetInt("port", 0),
            EnableDht = !parsed.GetBool("no-dht"),
            EnablePeerExchange = !parsed.GetBool("no-pex"),
            MaximumDownloadBytesPerSecond = parsed.GetLong("max-speed", 0L),
            MaximumUploadBytesPerSecond = parsed.GetLong("max-upload", 0L),
            SeedAfterDownload = parsed.GetBool("seed")
        };
        DateTime lastReport = DateTime.UtcNow;
        TorrentDownloadResult result = await TorrentDownloader.DownloadAsync(request, delegate(TorrentProgress progress)
        {
            if (json)
            {
                WriteEvent(new
                {
                    type = "torrent",
                    name = progress.Name,
                    total = progress.TotalBytes,
                    bytes = progress.BytesDownloaded,
                    percent = progress.TotalBytes > 0 ? progress.BytesDownloaded * 100.0 / progress.TotalBytes : (double?)null,
                    downloadSpeed = progress.DownloadBytesPerSecond,
                    uploadSpeed = progress.UploadBytesPerSecond,
                    peers = progress.ConnectedPeers,
                    metadata = progress.HasMetadata
                });
            }
            else if (!quiet)
            {
                DateTime utcNow = DateTime.UtcNow;
                if ((utcNow - lastReport).TotalMilliseconds >= 250)
                {
                    lastReport = utcNow;
                    double value = progress.TotalBytes > 0 ? progress.BytesDownloaded * 100.0 / progress.TotalBytes : 0;
                    Console.Out.Write($"\r{value,6:0.0}%  {ByteSize.Format(progress.BytesDownloaded)} / {ByteSize.Format(progress.TotalBytes)}  down {ByteSize.FormatSpeed(progress.DownloadBytesPerSecond)}  up {ByteSize.FormatSpeed(progress.UploadBytesPerSecond)}  peers {progress.ConnectedPeers}   ");
                }
            }
        }, cancellationToken);
        if (json)
        {
            WriteEvent(new
            {
                type = "completed",
                path = result.TargetPath,
                directory = result.IsDirectory,
                bytes = result.TotalBytes,
                name = result.Name
            });
        }
        else if (!quiet)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"saved {result.TotalBytes:N0} bytes to {result.TargetPath}");
        }
        return 0;
    }

    private static void AttachProgress(DownloadEngine engine, bool json, bool quiet)
    {
        engine.ProgressChanged += delegate(object? _, DownloadProgress progress)
        {
            if (json)
            {
                WriteEvent(new
                {
                    type = "progress",
                    bytes = progress.BytesDownloaded,
                    total = progress.TotalBytes,
                    percent = progress.Percent,
                    speed = progress.BytesPerSecond,
                    etaSeconds = progress.Eta?.TotalSeconds,
                    connections = progress.ActiveConnections
                });
            }
            else if (!quiet)
            {
                Console.Out.Write($"\r{progress.Percent.GetValueOrDefault(),6:0.0}%  {ByteSize.Format(progress.BytesDownloaded)} / {ByteSize.Format(progress.TotalBytes)}  {ByteSize.FormatSpeed(progress.BytesPerSecond)}  ETA {FormatEta(progress.Eta)}  conns {progress.ActiveConnections}   ");
            }
        };
        engine.StateChanged += delegate(object? _, JobStatus status)
        {
            if (json)
            {
                WriteEvent(new
                {
                    type = "state",
                    status = status.ToString().ToLowerInvariant()
                });
            }
        };
    }

    /// <summary>
    /// Reads the per-job connection ceiling. <c>--global-connections</c> is still
    /// accepted because the old name appeared in this tool's usage text and in
    /// scripts; it never meant "process-wide", so mapping it here keeps both
    /// names pointing at the same, honestly-named setting.
    /// </summary>
    private static int PerJobConnections(ParsedArguments parsed)
    {
        return parsed.GetInt("max-connections-per-job", parsed.GetInt("global-connections", 64));
    }

    private static BandwidthLimiter? CreateBandwidthLimiter(ParsedArguments parsed)
    {
        long num = parsed.GetLong("max-speed", 0L);
        if (num < 0)
        {
            throw new ArgumentException("--max-speed must be zero or positive");
        }
        return (num == 0L) ? null : new BandwidthLimiter(num);
    }

    private static RequestOptions BuildRequestOptions(ParsedArguments parsed)
    {
        return new RequestOptions
        {
            Referer = parsed.Get("referer"),
            Cookie = parsed.Get("cookie"),
            UserAgent = (parsed.Get("user-agent") ?? "PotatoDM/0.1"),
            UseProxy = !parsed.GetBool("no-proxy")
        };
    }

    private static Uri RequireUrl(ParsedArguments parsed)
    {
        if (parsed.Positionals.Count < 2)
        {
            throw new ArgumentException("a URL is required");
        }
        if (!Uri.TryCreate(parsed.Positionals[1], UriKind.Absolute, out var result) || (result.Scheme != Uri.UriSchemeHttp && result.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("invalid HTTP URL '" + parsed.Positionals[1] + "'");
        }
        return result;
    }

    private static int Fail(string message, int exitCode, bool json)
    {
        if (json)
        {
            WriteEvent(new
            {
                type = "failed",
                error = message,
                exitCode = exitCode
            });
        }
        else
        {
            Console.Error.WriteLine("error: " + message);
        }
        return exitCode;
    }

    private static void WriteEvent(object payload)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        Console.Out.Flush();
    }

    private static int MapExitCode(Exception exception) => exception switch
    {
        PotatoDownloadException download => download.ExitCode,
        // Deliberate fallback: torrent failures are not classified yet, so every one of
        // them lands in "未分类错误". Do not invent a mapping here before the throw sites
        // carry enough information to justify it.
        TorrentException => DownloadExitCode.Unexpected,
        OperationCanceledException => DownloadExitCode.Canceled,
        ArgumentException => DownloadExitCode.BadArguments,
        IOException => DownloadExitCode.FileAccess,
        UnauthorizedAccessException => DownloadExitCode.FileAccess,
        _ => DownloadExitCode.Unexpected,
    };

    private static string FormatEta(TimeSpan? eta)
    {
        if (!eta.HasValue)
        {
            return "--:--";
        }
        TimeSpan value = eta.Value;
        return (value.TotalHours >= 1.0) ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private static void PrintUsage()
    {
        Console.Out.WriteLine($"PotatoDM {Version}");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage:");
        Console.Out.WriteLine("  potatodm probe <url> [options]");
        Console.Out.WriteLine("  potatodm download <url> [options]");
        Console.Out.WriteLine("  potatodm resume <state.json|working-file> [options]");
        Console.Out.WriteLine("  potatodm torrent <magnet|.torrent> [-o directory] [options]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  -o, --output <path>            Target file or directory");
        Console.Out.WriteLine("  -c, --connections <n>          Parallel connections (default 8)");
        Console.Out.WriteLine("      --min-segment-size <bytes> Minimum segment size (default 1 MiB)");
        Console.Out.WriteLine("      --state <path>             Sidecar state file path");
        Console.Out.WriteLine("      --restart                  Discard existing state and start over");
        Console.Out.WriteLine("      --restart-on-change        Restart if the remote file changed");
        Console.Out.WriteLine("      --dynamic-segments         Let idle connections split slow ranges");
        Console.Out.WriteLine("      --sparse                   Keep unwritten ranges out of the working file");
        Console.Out.WriteLine("      --ffmpeg <path>            ffmpeg used to mux separate A/V tracks (PATH by default)");
        Console.Out.WriteLine("      --stall-timeout <sec>      No-data timeout (default 30)");
        Console.Out.WriteLine("      --max-retries <n>          Retries per segment (default 5)");
        Console.Out.WriteLine("      --max-per-host <n>         Per-host connection cap (default 8)");
        Console.Out.WriteLine("      --max-connections-per-job <n>");
        Console.Out.WriteLine("                                 Connections one job may hold at once (default 64)");
        Console.Out.WriteLine("      --global-connections <n>   Deprecated alias for --max-connections-per-job");
        Console.Out.WriteLine("      --max-speed <bytes/s>      Bandwidth cap (default unlimited)");
        Console.Out.WriteLine("      --referer <url>            Referer header");
        Console.Out.WriteLine("      --cookie <value>           Cookie header");
        Console.Out.WriteLine("      --user-agent <value>       User-Agent header");
        Console.Out.WriteLine("      --no-proxy                 Ignore the system proxy");
        Console.Out.WriteLine("      --json                     Emit NDJSON events");
        Console.Out.WriteLine("  -q, --quiet                    Suppress progress output");
        Console.Out.WriteLine("  -h, --help                     Show this help");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Torrent options:");
        Console.Out.WriteLine("      --port <n>                 TCP listen port (default: OS-assigned)");
        Console.Out.WriteLine("      --max-upload <bytes/s>     Upload cap (default unlimited)");
        Console.Out.WriteLine("      --seed                     Keep seeding until Ctrl+C");
        Console.Out.WriteLine("      --no-dht                   Disable DHT");
        Console.Out.WriteLine("      --no-pex                   Disable peer exchange");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Exit codes: 0 ok, 2 arguments, 3 probe, 4 HTTP, 5 file changed,");
        Console.Out.WriteLine("            6 file access, 7 state/lock, 8 unsupported media/DRM, 130 canceled.");
    }
}
