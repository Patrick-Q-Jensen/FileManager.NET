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
    private readonly NavigationState _state = new();
    private readonly Stack<string> _breadcrumb = new();

    // Null means "follow the global default"; set by SetLocalSortMode (Ctrl+O) to override it
    // for just this pane.
    private SortMode? _localSortMode;

    public NavigationController(
        IDirectoryService directoryService,
        IEntryFilter filter,
        IFileLauncher launcher,
        ISortSettingsService sortSettings)
    {
        _directoryService = directoryService;
        _filter = filter;
        _launcher = launcher;
        _sortSettings = sortSettings;

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

    public string CurrentDirectory => _state.CurrentDirectory;

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
        LoadDirectory(path);
    }

    /// <summary>
    /// Attempts to enter a directory without changing the current state when it cannot be read.
    /// Used to restore a prior session without opening tabs for disconnected or inaccessible paths.
    /// </summary>
    public bool TryEnterDirectory(string path)
    {
        try
        {
            var listing = _directoryService.Load(path);
            if (listing.Error is not null)
            {
                return false;
            }

            RestoredSelection = null;
            ApplyListing(path, listing);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore directory {Path}", path);
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
        LoadDirectory(_state.CurrentDirectory);
    }

    private void LoadDirectory(string path)
    {
        var stopwatch = Stopwatch.StartNew();
        var listing = _directoryService.Load(path);
        stopwatch.Stop();
        Log.Debug(
            "LoadDirectory {Path} took {ElapsedMs}ms ({EntryCount} entries)",
            path, stopwatch.ElapsedMilliseconds, listing.Entries.Count);

        ApplyListing(path, listing);
    }

    private void ApplyListing(string path, DirectoryListing listing)
    {
        _state.CurrentDirectory = path;
        _state.AllEntries = listing.Entries.ToList();
        _state.Query = string.Empty;
        _state.StatusMessage = listing.Error;
        ApplyFilter();
        Changed?.Invoke();
    }

    /// <summary>Navigates to the parent directory, if any.</summary>
    public void GoToParent()
    {
        var parent = Directory.GetParent(_state.CurrentDirectory);
        if (parent is not null)
        {
            // Remember the current directory name so the view can re-select it after moving up.
            RestoredSelection = Path.GetFileName(_state.CurrentDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            LoadDirectory(parent.FullName);
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
        if (entry.IsDirectory)
        {
            RestoredSelection = null;
            EnterDirectory(entry.FullPath);
        }
        else
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
        if (entry is { IsDirectory: true })
        {
            RestoredSelection = null;
            EnterDirectory(entry.FullPath);
        }
    }

    /// <summary>
    /// Re-reads the current directory from disk and, only if its contents actually changed,
    /// updates the entry list and raises <see cref="Changed"/>. Used by the periodic auto-refresh
    /// timer so a quiet directory never disturbs the current filter, selection, or scroll
    /// position. Unlike <see cref="LoadDirectory"/>, the query is preserved.
    /// </summary>
    public void RefreshFromDisk()
    {
        var listing = _directoryService.Load(_state.CurrentDirectory);
        if (listing.Error is not null || EntriesEqual(_state.AllEntries, listing.Entries))
        {
            return;
        }

        _state.AllEntries = listing.Entries.ToList();
        ApplyFilter();
        Changed?.Invoke();
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
