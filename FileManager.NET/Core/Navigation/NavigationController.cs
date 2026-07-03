using FileManager.NET.Core.FileSystem;
using FileManager.NET.Core.Filtering;
using FileManager.NET.Platform;

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
    private readonly NavigationState _state = new();
    private readonly Stack<string> _breadcrumb = new();

    public NavigationController(IDirectoryService directoryService, IEntryFilter filter, IFileLauncher launcher)
    {
        _directoryService = directoryService;
        _filter = filter;
        _launcher = launcher;
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
        var listing = _directoryService.Load(path);

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

    private void ApplyFilter() =>
        _state.FilteredEntries = _filter.Filter(_state.AllEntries, _state.Query);
}
