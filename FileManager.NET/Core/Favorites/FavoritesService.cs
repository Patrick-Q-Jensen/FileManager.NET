using System.Text.Json;

namespace FileManager.NET.Core.Favorites;

/// <summary>
/// Persists favorite directories to <c>%ProgramData%\FileManager.NET\favorites.json</c>.
/// The file is a plain JSON array of path strings. The in-memory set is protected by a lock
/// so async load and foreground Add calls are always safe to interleave.
/// </summary>
internal sealed class FavoritesService : IFavoritesService
{
    private static readonly string StorageDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FileManager.NET");

    private static readonly string FilePath =
        Path.Combine(StorageDirectory, "favorites.json");

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    private readonly HashSet<string> _favorites =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _lock = new(1, 1);

    // -------------------------------------------------------------------------
    // IFavoritesService
    // -------------------------------------------------------------------------

    public IReadOnlyCollection<string> Favorites
    {
        get
        {
            lock (_favorites)
            {
                return _favorites.ToArray();
            }
        }
    }

    public void BeginLoad() => Task.Run(LoadCoreAsync);

    public bool Contains(string path)
    {
        lock (_favorites)
        {
            return _favorites.Contains(path);
        }
    }

    public async Task<bool> AddAsync(string path)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_favorites)
            {
                if (!_favorites.Add(path))
                {
                    return false;
                }
            }

            await PersistAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task LoadCoreAsync()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            var paths = await JsonSerializer.DeserializeAsync<List<string>>(stream).ConfigureAwait(false);

            if (paths is null)
            {
                return;
            }

            lock (_favorites)
            {
                foreach (var path in paths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        _favorites.Add(path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Non-fatal: favorites simply start empty for this session.
        }
    }

    private async Task PersistAsync()
    {
        string[] snapshot;
        lock (_favorites)
        {
            snapshot = [.. _favorites.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
        }

        Directory.CreateDirectory(StorageDirectory);

        var tmp = FilePath + ".tmp";
        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions).ConfigureAwait(false);
        }

        File.Move(tmp, FilePath, overwrite: true);
    }
}
