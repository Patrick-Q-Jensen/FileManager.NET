using System.Text.Json;

namespace FileManager.NET.Core.Favorites;

/// <summary>
/// Persists favorite directories to <c>%ProgramData%\FileManager.NET\favorites.json</c>.
/// The file is a plain JSON array of path strings. Entries are stored in insertion order and
/// the list is capped at <see cref="IFavoritesService.MaxFavorites"/> entries. The in-memory
/// list is protected by a lock so async load and foreground mutations are always safe to
/// interleave.
/// </summary>
internal sealed class FavoritesService : IFavoritesService
{
    private static readonly string StorageDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FileManager.NET");

    private static readonly string FilePath =
        Path.Combine(StorageDirectory, "favorites.json");

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    // List preserves insertion order; uniqueness is enforced manually (n≤9, linear scan is fine).
    private readonly List<string> _favorites = [];

    private readonly SemaphoreSlim _lock = new(1, 1);

    // -------------------------------------------------------------------------
    // IFavoritesService
    // -------------------------------------------------------------------------

    public IReadOnlyList<string> Favorites
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
            return _favorites.Contains(path, StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<AddFavoriteResult> AddAsync(string path)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_favorites)
            {
                if (_favorites.Contains(path, StringComparer.OrdinalIgnoreCase))
                    return AddFavoriteResult.AlreadyExists;

                if (_favorites.Count >= IFavoritesService.MaxFavorites)
                    return AddFavoriteResult.AtCapacity;

                _favorites.Add(path);
            }

            await PersistAsync().ConfigureAwait(false);
            return AddFavoriteResult.Added;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RemoveAsync(string path)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            bool removed;
            lock (_favorites)
            {
                var index = _favorites.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    return false;

                _favorites.RemoveAt(index);
                removed = true;
            }

            await PersistAsync().ConfigureAwait(false);
            return removed;
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
                    if (!string.IsNullOrWhiteSpace(path)
                        && !_favorites.Contains(path, StringComparer.OrdinalIgnoreCase)
                        && _favorites.Count < IFavoritesService.MaxFavorites)
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
            snapshot = [.. _favorites];
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

