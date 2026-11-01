using System.Text.Json;
using PotatoDM.AppCore.Models;

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

    public JobRegistryStore(string? filePath = null)
    {
        FilePath = filePath ?? AppPaths.JobsFile;
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

            return JsonSerializer.Deserialize<List<JobRecord>>(json, JsonOptions)
                   ?? new List<JobRecord>();
        }
        catch (JsonException)
        {
            // A damaged registry must not prevent the app from starting.
            return new List<JobRecord>();
        }
        catch (IOException)
        {
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
