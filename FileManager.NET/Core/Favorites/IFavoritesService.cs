namespace FileManager.NET.Core.Favorites;

/// <summary>
/// Manages the user's list of favorite directories. The list is loaded from disk
/// asynchronously at startup via <see cref="LoadAsync"/> and persisted automatically
/// whenever a new favorite is added via <see cref="AddAsync"/>.
/// </summary>
internal interface IFavoritesService
{
    /// <summary>The current in-memory set of favorite directory paths.</summary>
    IReadOnlyCollection<string> Favorites { get; }

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
    /// Adds <paramref name="path"/> to the favorites list and persists the updated list to
    /// disk. Does nothing and returns <see langword="false"/> if <paramref name="path"/> is
    /// already present.
    /// </summary>
    Task<bool> AddAsync(string path);
}
