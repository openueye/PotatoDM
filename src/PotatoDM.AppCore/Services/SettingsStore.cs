using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using PotatoDM.AppCore.Models;
using PotatoDM.Core.Diagnostics;

namespace PotatoDM.AppCore.Services;

/// <summary>Persists user preferences; damaged files fall back to defaults.</summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore never creates a wait handle (AvailableWaitHandle is unused), " +
                    "so it owns nothing to release. The store lives for the process lifetime.")]
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogSink _log;

    /// <param name="filePath">Overrides the default settings path; used by tests.</param>
    /// <param name="log">
    /// Optional sink. A damaged settings file means every preference the user set
    /// is silently back to its default, which looks like the app "forgetting"
    /// things for no reason. Logged at Warning with the path so the file can be
    /// inspected before the next save overwrites it.
    /// </param>
    public SettingsStore(string? filePath = null, ILogSink? log = null)
    {
        FilePath = filePath ?? AppPaths.SettingsFile;
        _log = log ?? NullLogSink.Instance;
    }

    public string FilePath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                           ?? new AppSettings();
            settings.Normalize();

            return settings;
        }
        catch (JsonException ex)
        {
            _log.Warning(
                $"Settings file '{FilePath}' is not valid JSON; every preference falls back " +
                "to its default and the next save will overwrite the file.",
                ex);

            return new AppSettings();
        }
        catch (IOException ex)
        {
            _log.Warning(
                $"Settings file '{FilePath}' could not be read; every preference falls back " +
                "to its default for this session.",
                ex);

            return new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        AppSettings settings,
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
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
