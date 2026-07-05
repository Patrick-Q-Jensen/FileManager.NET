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

    public event Action<string>? ErrorOccurred;

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

            if (!await PersistAsync("Add favorite").ConfigureAwait(false))
            {
                lock (_favorites)
                    _favorites.Remove(path);

                return AddFavoriteResult.PersistFailed;
            }

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
            int index;
            lock (_favorites)
            {
                index = _favorites.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    return false;

                _favorites.RemoveAt(index);
            }

            if (!await PersistAsync("Remove favorite").ConfigureAwait(false))
            {
                // Persist failed: restore the entry at its original position.
                lock (_favorites)
                    _favorites.Insert(index, path);

                return false;
            }

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

        // Hold _lock for the entire read + populate window so that a concurrent RemoveAsync
        // or AddAsync cannot write a correct snapshot to disk and then have this method add
        // stale entries back into _favorites from the old file contents.
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
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
                ErrorOccurred?.Invoke($"Load favorites failed: {ex.Message}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> PersistAsync(string operation)
    {
        string[] snapshot;
        lock (_favorites)
        {
            snapshot = [.. _favorites];
        }

        try
        {
            Directory.CreateDirectory(StorageDirectory);

            var tmp = FilePath + ".tmp";
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions).ConfigureAwait(false);
            }

            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorOccurred?.Invoke($"{operation} failed: {ex.Message}");
            return false;
        }
    }
}

