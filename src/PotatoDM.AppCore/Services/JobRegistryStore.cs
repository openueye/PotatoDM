using System.Text.Json;
using PotatoDM.AppCore.Models;
using PotatoDM.Core.Diagnostics;

namespace PotatoDM.AppCore.Services;

/// <summary>Persists the list of jobs shown by the UI.</summary>
public sealed class JobRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogSink _log;

    /// <param name="filePath">Overrides the default registry path; used by tests.</param>
    /// <param name="log">
    /// Optional sink. A damaged registry used to mean the task list came back empty
    /// with no explanation, and the next save overwrote the damaged file so nothing
    /// could be recovered afterwards. Logged at Warning with the path.
    /// </param>
    public JobRegistryStore(string? filePath = null, ILogSink? log = null)
    {
        FilePath = filePath ?? AppPaths.JobsFile;
        _log = log ?? NullLogSink.Instance;
    }

    public string FilePath { get; }

    public async Task<List<JobRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(FilePath))
            {
                return new List<JobRecord>();
            }

            var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            var records = JsonSerializer.Deserialize<List<JobRecord>>(json, JsonOptions);

            if (records is null)
            {
                _log.Warning(
                    $"Job registry '{FilePath}' contains no list; the task list starts empty " +
                    "and the next save will overwrite the file.");

                return new List<JobRecord>();
            }

            return records;
        }
        catch (JsonException ex)
        {
            // A damaged registry must not prevent the app from starting.
            _log.Warning(
                $"Job registry '{FilePath}' is not valid JSON; the task list starts empty and " +
                "the next save will overwrite the file.",
                ex);

            return new List<JobRecord>();
        }
        catch (IOException ex)
        {
            _log.Warning(
                $"Job registry '{FilePath}' could not be read; the task list starts empty for " +
                "this session.",
                ex);

            return new List<JobRecord>();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<JobRecord> records,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(records, JsonOptions);
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
