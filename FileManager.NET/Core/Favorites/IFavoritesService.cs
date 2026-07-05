namespace FileManager.NET.Core.Favorites;

/// <summary>
/// Manages the user's ordered list of favorite directories (max <see cref="MaxFavorites"/>
/// entries). The list is loaded from disk asynchronously at startup via <see cref="BeginLoad"/>
/// and persisted automatically on every mutation.
/// </summary>
internal interface IFavoritesService
{
    /// <summary>Maximum number of entries the favorites list can hold.</summary>
    const int MaxFavorites = 9;

    /// <summary>
    /// Raised on any thread when a favorites operation fails. The string argument is a
    /// human-readable message naming the operation and the error.
    /// </summary>
    event Action<string> ErrorOccurred;

    /// <summary>The current in-memory list of favorite directory paths, in insertion order.</summary>
    IReadOnlyList<string> Favorites { get; }

    /// <summary>
    /// Starts loading favorites from disk in the background. Safe to call once at startup;
    /// the application works normally before this completes.
    /// </summary>
    void BeginLoad();

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="path"/> is already a favorite.
    /// </summary>
    bool Contains(string path);

    /// <summary>
    /// Adds <paramref name="path"/> to the favorites list and persists the updated list.
    /// Returns <see cref="AddFavoriteResult.Added"/> on success,
    /// <see cref="AddFavoriteResult.AlreadyExists"/> if the path is already present, or
    /// <see cref="AddFavoriteResult.AtCapacity"/> if <see cref="MaxFavorites"/> is reached.
    /// </summary>
    Task<AddFavoriteResult> AddAsync(string path);

    /// <summary>
    /// Removes <paramref name="path"/> from the favorites list and persists the updated list.
    /// Returns <see langword="true"/> if the path was present and removed.
    /// </summary>
    Task<bool> RemoveAsync(string path);
}
