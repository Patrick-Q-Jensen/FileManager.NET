using System.Text.Json;
using Serilog;

namespace FileManager.NET.Core.Sorting;

/// <summary>
/// Persists the global default sort order to <c>%ProgramData%\FileManager.NET\sort-settings.json</c>.
/// The file holds a single string naming the <see cref="SortMode"/>. Loaded synchronously from the
/// constructor: unlike favorites, the very first directory listing needs a sort order, so there is
/// nothing useful to render before the load completes.
/// </summary>
internal sealed class SortSettingsService : ISortSettingsService
{
    private static readonly string StorageDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FileManager.NET");

    private static readonly string FilePath =
        Path.Combine(StorageDirectory, "sort-settings.json");

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    private readonly object _lock = new();
    private SortMode _globalSortMode = SortMode.Name;

    public SortSettingsService()
    {
        LoadFromDisk();
    }

    public event Action<SortMode>? GlobalSortModeChanged;

    public SortMode GlobalSortMode
    {
        get
        {
            lock (_lock)
            {
                return _globalSortMode;
            }
        }
    }

    public void SetGlobalSortMode(SortMode mode)
    {
        lock (_lock)
        {
            if (_globalSortMode == mode)
            {
                return;
            }

            _globalSortMode = mode;
        }

        Persist(mode);
        GlobalSortModeChanged?.Invoke(mode);
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var json = File.ReadAllText(FilePath);
            var saved = JsonSerializer.Deserialize<SavedSettings>(json);
            if (saved is not null && Enum.TryParse<SortMode>(saved.GlobalSortMode, out var mode))
            {
                _globalSortMode = mode;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to the default sort order; there is nothing else to recover from here.
            Log.Warning(ex, "Failed to load sort settings from {FilePath}", FilePath);
        }
    }

    private static void Persist(SortMode mode)
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);

            var tmp = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(new SavedSettings(mode.ToString()), JsonOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persist failures are non-fatal: the chosen order still applies for this session.
            Log.Warning(ex, "Failed to persist sort settings to {FilePath}", FilePath);
        }
    }

    private sealed record SavedSettings(string GlobalSortMode);
}
