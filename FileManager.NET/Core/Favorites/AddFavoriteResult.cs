namespace FileManager.NET.Core.Favorites;

/// <summary>Result returned by <see cref="IFavoritesService.AddAsync"/>.</summary>
internal enum AddFavoriteResult
{
    /// <summary>The path was added successfully.</summary>
    Added,

    /// <summary>The path was already present in the favorites list.</summary>
    AlreadyExists,

    /// <summary>The favorites list is full (<see cref="IFavoritesService.MaxFavorites"/> reached).</summary>
    AtCapacity,
}
