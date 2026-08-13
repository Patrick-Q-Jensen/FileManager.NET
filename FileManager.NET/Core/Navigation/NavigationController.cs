using System.Diagnostics;
using FileManager.NET.Core.FileSystem;
using FileManager.NET.Core.Filtering;
using FileManager.NET.Core.Sorting;
using FileManager.NET.Platform;
using Serilog;

namespace FileManager.NET.Core.Navigation;

/// <summary>
/// Coordinates directory loading, live filtering, and activation over a <see cref="NavigationState"/>.
/// Raises <see cref="Changed"/> after every state mutation so views can refresh. Contains no
/// UI-framework types, keeping the core testable and reusable as features grow.
/// </summary>
internal sealed class NavigationController
{
    private readonly IDirectoryService _directoryService;
    private readonly IEntryFilter _filter;
    private readonly IFileLauncher _launcher;
    private readonly ISortSettingsService _sortSettings;
    private readonly ZipArchiveService _zipArchiveService;
    private readonly NavigationState _state = new();
    private List<FileSystemEntry>? _flattenEntries;
    private bool _flattenEntriesPublished;

    // Null means "follow the global default"; set by SetLocalSortMode (Ctrl+O) to override it
    // for just this pane.
    private SortMode? _localSortMode;

    public NavigationController(
        IDirectoryService directoryService,
        IEntryFilter filter,
        IFileLauncher launcher,
        ISortSettingsService sortSettings,
        ZipArchiveService zipArchiveService)
    {
        _directoryService = directoryService;
        _filter = filter;
        _launcher = launcher;
        _sortSettings = sortSettings;
        _zipArchiveService = zipArchiveService;

        // Only re-sort/refresh when this pane is actually following the global default;
        // panes with a local override are unaffected by other panes changing it.
        _sortSettings.GlobalSortModeChanged += mode =>
        {
            if (_localSortMode is null)
            {
                ApplyFilter();
                Changed?.Invoke();
            }
        };
    }

    /// <summary>The sort order currently in effect for this pane: the local override if set, else the global default.</summary>
    public SortMode EffectiveSortMode => _localSortMode ?? _sortSettings.GlobalSortMode;

    /// <summary>Overrides the sort order for this pane only, independent of the global default.</summary>
    public void SetLocalSortMode(SortMode mode)
    {
        _localSortMode = mode;
        ApplyFilter();
        Changed?.Invoke();
    }

    /// <summary>Raised after any change to the navigation state.</summary>
    public event Action? Changed;

    public NavigationLocation CurrentLocation => _state.Location;

    public string CurrentDirectory => _state.Location.ContainingDirectory;

    public string DisplayPath => _state.Location.DisplayPath;

    public bool IsArchive => _state.Location.IsArchive;

    public bool IsFlattened { get; private set; }

    public bool IsFlattening { get; private set; }

    public string Query => _state.Query;

    public string? StatusMessage => _state.StatusMessage;

    public IReadOnlyList<FileSystemEntry> FilteredEntries => _state.FilteredEntries;

    /// <summary>
    /// The child directory name to re-select after navigating up, or <c>null</c> when there is
    /// nothing to restore. Consumed and cleared by the view on each refresh.
    /// </summary>
    public string? RestoredSelection { get; private set; }

    /// <summary>Clears <see cref="RestoredSelection"/> after the view has consumed it.</summary>
    public void ConsumeRestoredSelection() => RestoredSelection = null;

    /// <summary>
    /// Loads <paramref name="path"/>, resets the filter, and raises <see cref="Changed"/>.
    /// Clears <see cref="RestoredSelection"/> unless called from <see cref="GoToParent"/>.
    /// Use this overload for all navigation that is NOT a "go up" action (favorites, drives,
    /// drill-down). For go-up, call <see cref="GoToParent"/> which sets the restored selection
    /// before calling this method.
    /// </summary>
    public void EnterDirectory(string path)
    {
        RestoredSelection = null;
        LoadLocation(NavigationLocation.Directory(path));
    }

    public void EnterArchive(string archivePath, string archiveDirectory = "")
    {
        RestoredSelection = null;
        LoadLocation(NavigationLocation.Archive(archivePath, archiveDirectory));
    }

    /// <summary>
    /// Attempts to enter a directory without changing the current state when it cannot be read.
    /// Used to restore a prior session without opening tabs for disconnected or inaccessible paths.
    /// </summary>
    public bool TryEnterDirectory(string path)
        => TryEnterLocation(NavigationLocation.Directory(path));

    public bool TryEnterLocation(NavigationLocation location)
    {
        try
        {
            var listing = LoadListing(location);
            if (listing.Error is not null)
            {
                return false;
            }

            RestoredSelection = null;
            ApplyListing(location, listing);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore location {Path}", location.DisplayPath);
            return false;
        }
    }

    /// <summary>
    /// Reloads the current directory and re-selects <paramref name="entryName"/> after the
    /// reload. Mirrors the <see cref="GoToParent"/> pattern of setting
    /// <see cref="RestoredSelection"/> before calling <see cref="LoadDirectory"/>.
    /// </summary>
    public void ReloadSelectingEntry(string entryName)
    {
        RestoredSelection = entryName;
        LoadLocation(_state.Location);
    }

    private void LoadLocation(NavigationLocation location)
    {
        var stopwatch = Stopwatch.StartNew();
        var listing = LoadListing(location);
        stopwatch.Stop();
        Log.Debug(
            "LoadLocation {Path} took {ElapsedMs}ms ({EntryCount} entries)",
            location.DisplayPath, stopwatch.ElapsedMilliseconds, listing.Entries.Count);

        ApplyListing(location, listing);
    }

    private DirectoryListing LoadListing(NavigationLocation location) =>
        location.IsArchive
            ? _zipArchiveService.LoadDirectory(location.PhysicalPath, location.ArchiveDirectory!)
            : _directoryService.Load(location.PhysicalPath);

    private void ApplyListing(NavigationLocation location, DirectoryListing listing)
    {
        ResetFlattenState();
        _state.Location = location;
        _state.AllEntries = listing.Entries.ToList();
        _state.Query = string.Empty;
        _state.StatusMessage = listing.Error;
        ApplyFilter();
        Changed?.Invoke();
    }

    /// <summary>Navigates to the parent directory, if any.</summary>
    public void GoToParent()
    {
        if (_state.Location.IsArchive)
        {
            var archiveDirectory = _state.Location.ArchiveDirectory!;
            if (archiveDirectory.Length == 0)
            {
                RestoredSelection = Path.GetFileName(_state.Location.PhysicalPath);
                LoadLocation(NavigationLocation.Directory(_state.Location.ContainingDirectory));
                return;
            }

            var separator = archiveDirectory.LastIndexOf('/');
            RestoredSelection = separator < 0
                ? archiveDirectory
                : archiveDirectory[(separator + 1)..];
            var parentDirectory = separator < 0 ? string.Empty : archiveDirectory[..separator];
            LoadLocation(NavigationLocation.Archive(_state.Location.PhysicalPath, parentDirectory));
            return;
        }

        var parent = Directory.GetParent(_state.Location.PhysicalPath);
        if (parent is not null)
        {
            // Remember the current directory name so the view can re-select it after moving up.
            RestoredSelection = Path.GetFileName(_state.Location.PhysicalPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            LoadLocation(NavigationLocation.Directory(parent.FullName));
        }
    }

    /// <summary>Appends a character to the live filter query and re-filters.</summary>
    public void AppendToQuery(char value)
    {
        _state.Query += value;
        ApplyFilter();
        Changed?.Invoke();
    }

    /// <summary>
    /// Removes the last filter character. Does nothing when the query is already empty so that
    /// deleting the filter never unintentionally navigates up; use <see cref="GoToParent"/> (Left)
    /// to move up and Esc to break out of filtering.
    /// </summary>
    public void Backspace()
    {
        if (_state.Query.Length == 0)
        {
            return;
        }

        _state.Query = _state.Query[..^1];
        ApplyFilter();
        Changed?.Invoke();
    }

    /// <summary>Clears the live filter query.</summary>
    public void ClearQuery()
    {
        if (_state.Query.Length == 0)
        {
            return;
        }

        _state.Query = string.Empty;
        ApplyFilter();
        Changed?.Invoke();
    }

    /// <summary>Opens the entry at <paramref name="index"/>: enters a directory or launches a file.</summary>
    public void Activate(int index)
    {
        if (index < 0 || index >= _state.FilteredEntries.Count)
        {
            return;
        }

        var entry = _state.FilteredEntries[index];
        if (TryEnterEntry(entry))
        {
            return;
        }

        if (!entry.IsArchiveEntry)
        {
            _state.StatusMessage = _launcher.Open(entry.FullPath);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Drills into the entry at <paramref name="index"/> when it is a directory. Files are ignored
    /// so the Right arrow only navigates into folders; use <see cref="Activate"/> (Enter) to launch files.
    /// </summary>
    public void DrillInto(int index)
    {
        var entry = GetEntry(index);
        if (entry is not null)
        {
            TryEnterEntry(entry);
        }
    }

    private bool TryEnterEntry(FileSystemEntry entry)
    {
        if (entry.IsArchiveEntry)
        {
            if (!entry.IsDirectory)
            {
                return false;
            }

            EnterArchive(entry.FullPath, entry.ArchiveEntryPath!);
            return true;
        }

        if (entry.IsDirectory)
        {
            EnterDirectory(entry.FullPath);
            return true;
        }

        if (string.Equals(Path.GetExtension(entry.Name), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            EnterArchive(entry.FullPath);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Re-reads the current directory from disk and, only if its contents actually changed,
    /// updates the entry list and raises <see cref="Changed"/>. Used by the periodic auto-refresh
    /// timer so a quiet directory never disturbs the current filter, selection, or scroll
    /// position. Unlike <see cref="LoadDirectory"/>, the query is preserved.
    /// </summary>
    public void RefreshFromDisk()
    {
        var listing = LoadListing(_state.Location);
        if (listing.Error is not null || EntriesEqual(_state.AllEntries, listing.Entries))
        {
            return;
        }

        ResetFlattenState();
        _state.AllEntries = listing.Entries.ToList();
        ApplyFilter();
        Changed?.Invoke();
    }

    /// <summary>Starts a progressively populated flattened view of the current directory tree.</summary>
    public bool BeginFlatten()
    {
        if (_state.Location.IsArchive)
        {
            _state.StatusMessage = "Flattening ZIP archive views is not supported.";
            Changed?.Invoke();
            return false;
        }

        IsFlattened = true;
        IsFlattening = true;
        _flattenEntries = [];
        _flattenEntriesPublished = false;
        _state.StatusMessage = "Flattening... 0 items found";
        Changed?.Invoke();
        return true;
    }

    /// <summary>Adds one worker batch to the active flattened view.</summary>
    public void AppendFlattenBatch(IReadOnlyList<FileSystemEntry> entries)
    {
        if (!IsFlattening || entries.Count == 0 || _flattenEntries is null)
        {
            return;
        }

        _flattenEntries.AddRange(entries);
        _state.AllEntries = _flattenEntries;
        _flattenEntriesPublished = true;
        _state.StatusMessage = $"Flattening... {_flattenEntries.Count:N0} items found";
        ApplyFilter();
        Changed?.Invoke();
    }

    /// <summary>Finishes the active flattened view and reports any paths that were skipped.</summary>
    public void CompleteFlatten(DirectoryTreeResult result)
    {
        if (!IsFlattening || _flattenEntries is null)
        {
            return;
        }

        PublishEmptyFlattenViewIfNeeded();
        IsFlattening = false;
        _state.StatusMessage = result.PathsSkipped == 0
            ? $"Flattened {result.EntriesFound:N0} items. Ctrl+E to restore."
            : $"Flattened {result.EntriesFound:N0} items; {result.PathsSkipped:N0} paths skipped. Ctrl+E to restore.";
        Changed?.Invoke();
    }

    /// <summary>Keeps any entries already found while surfacing an unexpected flattening failure.</summary>
    public void FailFlatten(string message)
    {
        if (!IsFlattening || _flattenEntries is null)
        {
            return;
        }

        PublishEmptyFlattenViewIfNeeded();
        IsFlattening = false;
        _state.StatusMessage = $"Flattening stopped: {message}";
        Changed?.Invoke();
    }

    /// <summary>Restores the ordinary listing for the current directory.</summary>
    public void ExitFlatten()
    {
        if (IsFlattened)
        {
            LoadLocation(_state.Location);
        }
    }

    private void PublishEmptyFlattenViewIfNeeded()
    {
        if (_flattenEntriesPublished || _flattenEntries is null)
        {
            return;
        }

        _state.AllEntries = _flattenEntries;
        _flattenEntriesPublished = true;
        ApplyFilter();
    }

    private void ResetFlattenState()
    {
        IsFlattened = false;
        IsFlattening = false;
        _flattenEntries = null;
        _flattenEntriesPublished = false;
    }

    // Order-independent comparison: enumeration order can vary between passes even when
    // nothing has changed, so an unordered set comparison avoids spurious refreshes.
    private static bool EntriesEqual(IReadOnlyList<FileSystemEntry> before, IReadOnlyList<FileSystemEntry> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        return before.ToHashSet().SetEquals(after);
    }

    /// <summary>Returns the entry at <paramref name="index"/>, or <c>null</c> when out of range.</summary>
    public FileSystemEntry? GetEntry(int index) =>
        index >= 0 && index < _state.FilteredEntries.Count
            ? _state.FilteredEntries[index]
            : null;

    /// <summary>Sets the status message shown to the user and notifies observers.</summary>
    public void SetStatus(string? message)
    {
        _state.StatusMessage = message;
        Changed?.Invoke();
    }

    private void ApplyFilter()
    {
        var filtered = _filter.Filter(_state.AllEntries, _state.Query).ToList();
        filtered.Sort((a, b) => EntryComparer.Compare(a, b, EffectiveSortMode));
        _state.FilteredEntries = filtered;
    }
}
