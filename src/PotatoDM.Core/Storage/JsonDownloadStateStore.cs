using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using PotatoDM.Core.Diagnostics;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Models;

namespace PotatoDM.Core.Storage;

public sealed class JsonDownloadStateStore : IDownloadStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ILogSink _log;

    /// <param name="log">
    /// Optional sink. Defaults to <see cref="NullLogSink"/> so every existing
    /// construction site keeps working unchanged.
    /// </param>
    public JsonDownloadStateStore(ILogSink? log = null)
    {
        _log = log ?? NullLogSink.Instance;
    }

    public async Task<JobState?> LoadAsync(string statePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
        {
            var orphanBackup = statePath + ".bak";
            if (File.Exists(orphanBackup))
            {
                _log.Debug($"State file '{statePath}' is gone; recovering from its backup.");
                return await ReadValidatedAsync(orphanBackup, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        try
        {
            return await ReadValidatedAsync(statePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidDataException)
        {
            var backupPath = statePath + ".bak";
            if (File.Exists(backupPath))
            {
                _log.Warning(
                    $"State file '{statePath}' is unreadable; falling back to '{backupPath}'.",
                    ex);

                try
                {
                    return await ReadValidatedAsync(backupPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception backupFailure) when (
                    backupFailure is JsonException or NotSupportedException or InvalidDataException)
                {
                    _log.Error(
                        $"Both '{statePath}' and its backup are unreadable; the job cannot be resumed.",
                        backupFailure);

                    throw new StateException(
                        $"Both '{statePath}' and its backup are corrupt.", backupFailure);
                }
            }

            _log.Warning($"State file '{statePath}' is unreadable and has no backup.", ex);
            throw new StateException($"State file '{statePath}' is corrupt.", ex);
        }
        catch (IOException ex)
        {
            throw new FileAccessException($"Could not read state file '{statePath}'.", ex);
        }
    }

    public async Task SaveAsync(string statePath, JobState state, CancellationToken cancellationToken)
    {
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
        var temporaryPath = statePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous);

            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            ReplaceWithRetry(temporaryPath, statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FileAccessException($"Could not write state file '{statePath}'.", ex);
        }
    }

    private static async Task<JobState> ReadValidatedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new InvalidDataException($"State file '{path}' is empty.");
        }

        var state = JsonSerializer.Deserialize<JobState>(bytes, SerializerOptions)
                    ?? throw new InvalidDataException($"State file '{path}' contained no data.");

        if (state.SchemaVersion != JobState.CurrentSchemaVersion)
        {
            throw new StateException(
                $"Unsupported state schema version {state.SchemaVersion} " +
                $"(expected {JobState.CurrentSchemaVersion}).");
        }

        return state;
    }

    private static void ReplaceWithRetry(string temporaryPath, string destinationPath)
    {
        var backupPath = destinationPath + ".bak";

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(temporaryPath, destinationPath, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, destinationPath);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 4)
                {
                    throw;
                }

                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
