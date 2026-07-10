namespace FileManager.NET.Core.Sorting;

/// <summary>
/// Manages the application-wide default sort order. Panes that have not chosen a local override
/// follow this value. Persisted to <c>%ProgramData%\FileManager.NET\sort-settings.json</c> and
/// loaded synchronously at startup, since the very first directory listing needs a sort order.
/// </summary>
internal interface ISortSettingsService
{
    /// <summary>The current global default sort order.</summary>
    SortMode GlobalSortMode { get; }

    /// <summary>Raised after <see cref="GlobalSortMode"/> changes, on the calling thread.</summary>
    event Action<SortMode>? GlobalSortModeChanged;

    /// <summary>Sets and persists the global default sort order.</summary>
    void SetGlobalSortMode(SortMode mode);
}
