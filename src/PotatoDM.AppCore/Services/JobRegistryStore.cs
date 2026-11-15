using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using PotatoDM.AppCore.Models;
using PotatoDM.Core.Diagnostics;

namespace PotatoDM.AppCore.Services;

/// <summary>Persists the list of jobs shown by the UI.</summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore never creates a wait handle (AvailableWaitHandle is unused), " +
                    "so it owns nothing to release. The store lives for the process lifetime.")]
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
    /// Optional sink for reporting registry recovery failures.
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
                _log.Warning($"Job registry '{FilePath}' contains no list.");
                PreserveDamagedRegistry();
                return new List<JobRecord>();
            }

            return records;
        }
        catch (JsonException ex)
        {
            _log.Warning($"Job registry '{FilePath}' is not valid JSON.", ex);
            PreserveDamagedRegistry();
            return new List<JobRecord>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(FilePath))
            {
                return new List<JobRecord>();
            }

            _log.Warning($"Job registry '{FilePath}' could not be read.", ex);
            PreserveDamagedRegistry();
            return new List<JobRecord>();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Location of the original registry preserved after a failed parse.</summary>
    public string? DamagedBackupPath { get; private set; }

    /// <summary>
    /// True when a damaged or unreadable registry could not be moved aside. The
    /// original stays at <see cref="FilePath"/>; the app still starts empty but
    /// the caller should tell the user before a later save replaces it.
    /// </summary>
    public bool DamagedRegistryUnpreserved { get; private set; }

    private void PreserveDamagedRegistry()
    {
        var backup = FilePath + ".damaged-" + Guid.NewGuid().ToString("N");

        try
        {
            File.Move(FilePath, backup);
            DamagedBackupPath = backup;
            _log.Warning($"Damaged job registry preserved at '{backup}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DamagedRegistryUnpreserved = true;
            _log.Error(
                $"Damaged job registry could not be preserved; the original remains at '{FilePath}'.",
                ex);
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
