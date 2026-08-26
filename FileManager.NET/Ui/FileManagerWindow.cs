using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using FileManager.NET.Core.Favorites;
using FileManager.NET.Core.FileSystem;
using FileManager.NET.Core.Git;
using FileManager.NET.Core.Navigation;
using FileManager.NET.Core.Sorting;
using FileManager.NET.Core.Undo;
using FileManager.NET.Platform;

namespace FileManager.NET.Ui;

/// <summary>
/// Single-pane file manager view: a filter line, a virtualized list of entries, and a status
/// line. The current directory is shown in the window title. Printable keys are captured by
/// <see cref="FilterListView"/> to drive substring filtering (File Pilot style); all other keys
/// are routed through <see cref="OnEntryKeyDown"/> for navigation and activation.
/// </summary>
internal sealed class FileManagerWindow : Window
{
    // Upper bound for the path portion of the title before its leading segments are elided.
    private const int MaxPathTitleLength = 48;
    private static readonly TimeSpan FlattenRefreshInterval = TimeSpan.FromMilliseconds(100);

    // How often the current directory is silently re-checked for external changes (files added,
    // removed, or renamed by another process). Kept fairly relaxed since this is a convenience
    // feature, not a live filesystem watch.
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Set by the host (FileManagerTabs) to keep all tab headers the same width. Defaults to 20
    // until the host computes the available-width-divided-by-tab-count value.
    internal int TabTitleWidth { get; set; } = 20;

    private readonly IApplication _app;
    private readonly NavigationController _controller;
    private readonly IDirectoryService _directoryService;
    private readonly IFavoritesService _favoritesService;
    private readonly ISortSettingsService _sortSettingsService;
    private readonly IFileLauncher _fileLauncher;
    private readonly ZipArchiveService _zipArchiveService;
    private readonly UndoHistory _undoHistory;
    private readonly IGitRepositoryService _gitRepositoryService;
    private readonly Label _filterLabel;
    private readonly FilterListView _listView;
    private readonly Label _statusLabel;

    // Tracks the entry set currently rendered so status-only refreshes (e.g. after a command)
    // don't rebuild the list and reset the selection back to the top.
    private IReadOnlyList<FileSystemEntry>? _renderedEntries;

    // Tracks the directory the tab header was last drawn for. When it changes (drill-down or
    // move to parent) DirectoryChanged is raised so the host can refresh the tab strip.
    private string? _renderedDirectory;

    // Token for the periodic auto-refresh timeout, used to unregister it on disposal.
    private object? _autoRefreshToken;
    private bool _archiveRefreshRunning;
    private FlattenOperation? _flattenOperation;
    private object? _flattenRefreshToken;
    private CancellationTokenSource? _gitProbeCancellation;
    private bool _disposed;

    /// <summary>
    /// Raised when this tab has navigated to a different directory (and therefore its tab header
    /// title changed). The host uses this to refresh the whole tab strip so headers reflow without
    /// overlapping. Not raised for filter edits or status-only updates.
    /// </summary>
    internal event Action? DirectoryChanged;

    /// <summary>Set by the host to handle Ctrl+1-9 tab switching from within this pane.</summary>
    internal Action<int>? SwitchToTab { get; set; }

    /// <summary>Set by the host to handle Ctrl+T duplicate-tab from within this pane.</summary>
    internal Action? DuplicateTab { get; set; }

    /// <summary>Set by the host to handle Ctrl+W close-tab from within this pane.</summary>
    internal Action? CloseTab { get; set; }

    /// <summary>Set by the host to handle Ctrl+Tab next-tab from within this pane.</summary>
    internal Action? CycleTab { get; set; }

    /// <summary>Set by the host to move to the adjacent tab; -1 is left and 1 is right.</summary>
    internal Action<int>? MoveToAdjacentTab { get; set; }

    /// <summary>The directory currently displayed in this tab.</summary>
    internal string CurrentDirectory => _controller.CurrentDirectory;

    internal NavigationLocation CurrentLocation => _controller.CurrentLocation;

    public FileManagerWindow(
        IApplication app,
        NavigationController controller,
        IDirectoryService directoryService,
        IFavoritesService favoritesService,
        ISortSettingsService sortSettingsService,
        IFileLauncher fileLauncher,
        ZipArchiveService zipArchiveService,
        UndoHistory undoHistory,
        IGitRepositoryService gitRepositoryService)
    {
        _app = app;
        _controller = controller;
        _directoryService = directoryService;
        _favoritesService = favoritesService;
        _sortSettingsService = sortSettingsService;
        _fileLauncher = fileLauncher;
        _zipArchiveService = zipArchiveService;
        _undoHistory = undoHistory;
        _gitRepositoryService = gitRepositoryService;

        _filterLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
        };

        _listView = new FilterListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            MarkMultiple = true,
        };

        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
        };

        Add(_filterLabel, _listView, _statusLabel);

        // FilterListView routes printable keystrokes to CharacterTyped for live filtering; every
        // other key flows through KeyDown for navigation and activation.
        _listView.CharacterTyped += OnCharacterTyped;
        _listView.KeyDown += OnEntryKeyDown;
        _controller.Changed += Refresh;
        _favoritesService.ErrorOccurred += OnFavoritesError;

        // The tab host loads the initial directory before constructing this pane so restored
        // directories can be validated without a second filesystem read. Render that existing
        // state now because its Changed notification occurred before this subscription.
        Refresh();
        _listView.SetFocus();

        _autoRefreshToken = _app.AddTimeout(AutoRefreshInterval, OnAutoRefreshTimer);
    }

    // Measures the full handling time for a navigation key (including any synchronous UI
    // refresh triggered by NavigationController.Changed), so slowdowns can be attributed to
    // the disk read (already logged separately by the controller) versus UI work.
    // Runs on the main UI thread (per IApplication.AddTimeout), so it's safe to touch the
    // controller/views directly. Returning true keeps the timer repeating.
    private bool OnAutoRefreshTimer()
    {
        if (_controller.IsFlattened)
        {
            return true;
        }

        if (_controller.IsArchive)
        {
            BeginArchiveAutoRefresh();
            return true;
        }

        try
        {
            _controller.RefreshFromDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort background check; a transient I/O failure here should never surface
            // as a crash or disrupt what the user is doing.
            Log.Warning(ex, "Auto-refresh failed for {Directory}", _controller.CurrentDirectory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected auto-refresh failure for {Directory}", _controller.CurrentDirectory);
        }

        return true;
    }

    private void BeginArchiveAutoRefresh()
    {
        if (_archiveRefreshRunning)
        {
            return;
        }

        _archiveRefreshRunning = true;
        var location = _controller.CurrentLocation;
        _ = Task.Run(() => _zipArchiveService.LoadDirectory(
                location.PhysicalPath,
                location.ArchiveDirectory!))
            .ContinueWith(task =>
            {
                _app.Invoke(() =>
                {
                    try
                    {
                        if (task.IsFaulted)
                        {
                            Log.Error(task.Exception, "Unexpected auto-refresh failure for ZIP archive {ArchivePath}",
                                location.PhysicalPath);
                        }
                        else if (_controller.CurrentLocation == location)
                        {
                            // The background load refreshed the index; this second load is a
                            // cache hit and only compares the resulting entry metadata.
                            _controller.RefreshFromDisk();
                        }
                    }
                    finally
                    {
                        _archiveRefreshRunning = false;
                    }
                });
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            CancelGitProbe();
            StopFlattenOperation();
            _controller.Changed -= Refresh;
            _favoritesService.ErrorOccurred -= OnFavoritesError;

            if (_autoRefreshToken is not null)
            {
                _app.RemoveTimeout(_autoRefreshToken);
                _autoRefreshToken = null;
            }
        }

        base.Dispose(disposing);
    }

    private void BeginGitProbe(NavigationLocation location)
    {
        CancelGitProbe();
        if (location.IsArchive || !_gitRepositoryService.IsAvailable)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        _gitProbeCancellation = cancellation;

        Task<GitRepositoryInfo?> task;
        try
        {
            task = Task.Run(
                () => _gitRepositoryService.DetectAsync(
                    location.PhysicalPath,
                    cancellationToken),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            ReleaseGitProbe(cancellation);
            Log.Error(ex, "Failed to start Git repository detection for {Directory}", location.PhysicalPath);
            return;
        }

        _ = task.ContinueWith(completedTask =>
        {
            if (_disposed)
            {
                ReleaseGitProbe(cancellation);
                return;
            }

            _app.Invoke(() =>
            {
                try
                {
                    if (!ReferenceEquals(_gitProbeCancellation, cancellation)
                        || cancellationToken.IsCancellationRequested
                        || _controller.CurrentLocation != location)
                    {
                        return;
                    }

                    if (completedTask.IsCompletedSuccessfully)
                    {
                        _controller.SetGitRepository(location, completedTask.Result);
                    }
                    else if (!completedTask.IsCanceled)
                    {
                        Log.Error(
                            completedTask.Exception,
                            "Git repository detection failed unexpectedly for {Directory}",
                            location.PhysicalPath);
                    }
                }
                finally
                {
                    ReleaseGitProbe(cancellation);
                }
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void CancelGitProbe()
    {
        var cancellation = Interlocked.Exchange(ref _gitProbeCancellation, null);
        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to cancel Git repository detection");
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    private void ReleaseGitProbe(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _gitProbeCancellation,
                    null,
                    cancellation),
                cancellation))
        {
            cancellation.Dispose();
        }
    }

    private void OnCharacterTyped(char character) => _controller.AppendToQuery(character);

    private void OnFavoritesError(string message) =>
        _app.Invoke(() => _controller.SetStatus(message));

    // Timestamped on entry so we can measure per-key latency end to end: our own handler
    // (below) plus, via the queued follow-up, any native handling (e.g. ListView Up/Down
    // navigation) and redraw that happen afterward in this same main-loop pass.
    private void OnEntryKeyDown(object? sender, Key key)
    {
        var keyCode = key.KeyCode;
        var receivedAt = Stopwatch.GetTimestamp();

        HandleEntryKeyDown(key);

        var handlerMs = Stopwatch.GetElapsedTime(receivedAt).TotalMilliseconds;
        Log.Debug("Key {KeyCode}: our handler took {HandlerMs:F1}ms", keyCode, handlerMs);

        // Queued to run on a later main-loop iteration, after Terminal.Gui has finished any
        // native key handling and redraw triggered by this key. Comparing this to the handler
        // time above shows whether a slow key is caused by our code or by framework/redraw work.
        _app.Invoke(() =>
        {
            var settledMs = Stopwatch.GetElapsedTime(receivedAt).TotalMilliseconds;
            Log.Debug("Key {KeyCode}: settled after {SettledMs:F1}ms (key received to UI idle)", keyCode, settledMs);
        });
    }

    private void HandleEntryKeyDown(Key key)
    {
        // Ctrl-chords are commands, never filter input. Dispatch them first so no command key
        // ever falls through to navigation or the live filter.
        if (key.IsCtrl)
        {
            if (TryHandleCommand(key))
            {
                key.Handled = true;
            }

            return;
        }

        switch (key.KeyCode)
        {
            case KeyCode.CursorUp:
            case KeyCode.CursorDown:
            case KeyCode.PageUp:
            case KeyCode.PageDown:
            case KeyCode.Home:
            case KeyCode.End:
                return; // Let the ListView perform native, virtualized navigation.

            case KeyCode.Enter:
                ActivateSelectedEntry();
                key.Handled = true;
                return;

            case KeyCode.CursorRight:
                DrillIntoSelectedEntry();
                key.Handled = true;
                return;

            case KeyCode.CursorLeft:
                _controller.GoToParent();
                key.Handled = true;
                return;

            case KeyCode.Delete:
                ShowDeleteConfirmDialog();
                key.Handled = true;
                return;

            case KeyCode.Backspace:
                _controller.Backspace();
                key.Handled = true;
                return;

            case KeyCode.Esc:
                // Esc breaks out of filtering mode without exiting the application. This keeps
                // deleting the filter from ever moving up a directory.
                if (_controller.Query.Length > 0)
                {
                    _controller.ClearQuery();
                }

                key.Handled = true;
                return;

            default:
                int tabIndex = GetFKeyTabIndex(key.KeyCode);
                if (tabIndex >= 0)
                {
                    SwitchToTab?.Invoke(tabIndex);
                    key.Handled = true;
                }

                return;
        }
    }

    /// <summary>
    /// Handles a Ctrl-initiated command from <paramref name="key"/>. CtrlMask and AltMask are used to get the base key.
    /// </summary>
    private bool TryHandleCommand(Key key)
    {
        bool alt = key.IsAlt;
        var baseKey = key.KeyCode & ~(KeyCode.CtrlMask | KeyCode.AltMask);

        switch (baseKey)
        {
            case KeyCode.Q:
                _app.RequestStop();
                return true;

            case KeyCode.F when alt:
                AddCurrentDirectoryToFavorites();
                return true;

            case KeyCode.F:
                ShowFavoritesDialog();
                return true;

            case KeyCode.P when alt:
                ShowPropertiesDialog();
                return true;

            case KeyCode.P:
                CopySelectedPathToClipboard();
                return true;

            case KeyCode.N:
                CopySelectedNameToClipboard();
                return true;

            case KeyCode.C when alt:
                ShowCreateFileDialog();
                return true;

            case KeyCode.C:
                CopySelectedItemToClipboard();
                return true;

            case KeyCode.V:
                PasteFromClipboard();
                return true;

            // Ctrl+Alt+X runs an arbitrary command line in the current directory (git, copilot, ...),
            // whereas plain Ctrl+X executes the selected entry itself.
            case KeyCode.X when alt:
                ShowRunCommandDialog();
                return true;

            case KeyCode.X:
                ShowExecuteDialog();
                return true;

            case KeyCode.Z when alt:
                CreateZipArchive();
                return true;

            case KeyCode.Z:
                UndoLastOperation();
                return true;

            case KeyCode.D:
                ShowDrivesDialog();
                return true;

            case KeyCode.E when !alt:
                ToggleFlattenedView();
                return true;

            case KeyCode.R:
                ShowRenameDialog();
                return true;

            case KeyCode.T:
                DuplicateTab?.Invoke();
                return true;

            case KeyCode.W:
                CloseTab?.Invoke();
                return true;

            case KeyCode.Tab:
                CycleTab?.Invoke();
                return true;

            case KeyCode.CursorLeft:
                MoveToAdjacentTab?.Invoke(-1);
                return true;

            case KeyCode.CursorRight:
                MoveToAdjacentTab?.Invoke(1);
                return true;

            case KeyCode.G:
                ShowMoveToDialog();
                return true;

            case KeyCode.O when alt:
                ShowSortDialog(global: true);
                return true;

            case KeyCode.O:
                ShowSortDialog(global: false);
                return true;

            case KeyCode.I when alt:
                MoveSelection(-1);
                return true;

            case KeyCode.K when alt:
                MoveSelection(1);
                return true;

            case KeyCode.J when alt:
                _controller.GoToParent();
                return true;

            case KeyCode.L when alt:
                DrillIntoSelectedEntry();
                return true;

            case KeyCode.H when alt:
                ShowHelpDialog();
                return true;

            // Ctrl+B ("box select") toggles marking mode. Ctrl+M was ruled out because it's
            // indistinguishable from Enter at the terminal-input level (both send ASCII CR), and
            // Ctrl+Alt+M was ruled out because some keyboard layouts/drivers report Ctrl+Alt
            // chords as an AltGr-composed character instead of separate modifier flags.
            case KeyCode.B:
                ToggleMarkingMode();
                return true;

            default:
                return false;
        }
    }

    // Ctrl+B toggles "box select mode": while enabled, Space checks/unchecks individual entries
    // (the ListView's built-in mark toggle, shown via ShowMarks) instead of Space falling
    // through to the live filter as a typed character.
    private void ToggleMarkingMode()
    {
        _listView.ShowMarks = !_listView.ShowMarks;
        _controller.SetStatus(_listView.ShowMarks ? "Marking mode on (Space to select)" : "Marking mode off");
        SetNeedsDraw();
    }

    private void ToggleFlattenedView()
    {
        if (_controller.IsFlattened)
        {
            StopFlattenOperation();

            try
            {
                _controller.ExitFlatten();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to restore directory view for {Directory}", _controller.CurrentDirectory);
                _controller.SetStatus($"Could not restore directory view: {ex.Message}");
            }

            return;
        }

        StartFlattenOperation();
    }

    private void StartFlattenOperation()
    {
        if (!_controller.BeginFlatten())
        {
            return;
        }

        var operation = new FlattenOperation();
        _flattenOperation = operation;
        _flattenRefreshToken = _app.AddTimeout(
            FlattenRefreshInterval,
            () => PublishFlattenProgress(operation));
        var rootPath = _controller.CurrentDirectory;

        _ = Task.Run(() => RunFlattenWorker(operation, rootPath));
    }

    private void RefreshAfterMutation(Action updateFlattenedView, Action updateDirectoryView)
    {
        if (!_controller.IsFlattened)
        {
            updateDirectoryView();
            return;
        }

        var enumerationWasRunning = _controller.IsFlattening;
        StopFlattenOperation();

        if (enumerationWasRunning)
        {
            StartFlattenOperation();
        }
        else
        {
            updateFlattenedView();
        }
    }

    private void RunFlattenWorker(FlattenOperation operation, string rootPath)
    {
        try
        {
            var result = _directoryService.EnumerateTree(
                rootPath,
                batch =>
                {
                    lock (operation.SyncRoot)
                    {
                        operation.PendingEntries.AddRange(batch);
                    }
                },
                operation.Cancellation.Token);

            lock (operation.SyncRoot)
            {
                operation.Result = result;
            }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (operation.SyncRoot)
            {
                operation.Error = ex;
            }
        }
        finally
        {
            bool disposeCancellation;
            lock (operation.SyncRoot)
            {
                operation.Completed = true;
                disposeCancellation = operation.Abandoned;
            }

            if (disposeCancellation)
            {
                operation.Cancellation.Dispose();
            }
        }
    }

    private bool PublishFlattenProgress(FlattenOperation operation)
    {
        if (!ReferenceEquals(operation, _flattenOperation))
        {
            return false;
        }

        FileSystemEntry[] pendingEntries;
        DirectoryTreeResult? result;
        Exception? error;
        bool completed;

        lock (operation.SyncRoot)
        {
            pendingEntries = operation.PendingEntries.ToArray();
            operation.PendingEntries.Clear();
            result = operation.Result;
            error = operation.Error;
            completed = operation.Completed;
        }

        if (pendingEntries.Length > 0)
        {
            _controller.AppendFlattenBatch(pendingEntries);
        }

        if (!completed)
        {
            return true;
        }

        _flattenOperation = null;
        _flattenRefreshToken = null;
        ReleaseFlattenOperation(operation, cancel: false);

        if (error is not null)
        {
            Log.Error(error, "Unexpected failure flattening directory {Directory}", _controller.CurrentDirectory);
            _controller.FailFlatten(error.Message);
        }
        else if (result is not null)
        {
            _controller.CompleteFlatten(result);
        }

        return false;
    }

    private void StopFlattenOperation()
    {
        var operation = _flattenOperation;
        if (operation is null)
        {
            return;
        }

        _flattenOperation = null;
        if (_flattenRefreshToken is not null)
        {
            _app.RemoveTimeout(_flattenRefreshToken);
            _flattenRefreshToken = null;
        }

        ReleaseFlattenOperation(operation, cancel: true);
    }

    private static void ReleaseFlattenOperation(FlattenOperation operation, bool cancel)
    {
        bool disposeCancellation;
        lock (operation.SyncRoot)
        {
            operation.Abandoned = true;
            if (cancel && !operation.Completed)
            {
                operation.Cancellation.Cancel();
            }

            disposeCancellation = operation.Completed;
        }

        if (disposeCancellation)
        {
            operation.Cancellation.Dispose();
        }
    }

    private void MoveSelection(int delta)
    {
        var count = _renderedEntries?.Count ?? 0;
        if (count == 0)
            return;

        var current = _listView.SelectedItem ?? 0;
        var next = Math.Clamp(current + delta, 0, count - 1);

        if (next == current)
            return;

        _listView.SelectedItem = next;
        _listView.EnsureSelectedItemVisible();
    }

    // Attaches Ctrl+Alt+I (up), Ctrl+Alt+K (down), and optionally Ctrl+Alt+L (confirm) to a
    // dialog ListView. J is intentionally omitted: going to parent has no meaning in a picker.
    private static void AttachVimNavigation(ListView listView, int count, Action? onAccept = null)
    {
        listView.KeyDown += (_, key) =>
        {
            if (!key.IsCtrl || !key.IsAlt)
                return;

            var baseKey = key.KeyCode & ~(KeyCode.CtrlMask | KeyCode.AltMask);

            if (baseKey == KeyCode.L && onAccept is not null)
            {
                onAccept();
                key.Handled = true;
                return;
            }

            int delta = baseKey switch { KeyCode.I => -1, KeyCode.K => 1, _ => 0 };
            if (delta == 0 || count == 0)
                return;

            var current = listView.SelectedItem ?? 0;
            var next = Math.Clamp(current + delta, 0, count - 1);
            if (next == current)
                return;

            listView.SelectedItem = next;
            listView.EnsureSelectedItemVisible();
            key.Handled = true;
        };
    }

    private static int GetFKeyTabIndex(KeyCode key)
    {
        return key switch
        {
            KeyCode.F1 => 0,
            KeyCode.F2 => 1,
            KeyCode.F3 => 2,
            KeyCode.F4 => 3,
            KeyCode.F5 => 4,
            KeyCode.F6 => 5,
            KeyCode.F7 => 6,
            KeyCode.F8 => 7,
            KeyCode.F9 => 8,
            _ => -1,
        };
    }

    private void CopySelectedNameToClipboard()
    {
        var entries = GetSelectedEntries(excludeParent: false);
        if (entries.Count == 0)
        {
            _controller.SetStatus("Nothing selected to copy.");
            return;
        }

        var text = string.Join('\n', entries.Select(e => e.Name));
        _controller.SetStatus(_app.Clipboard.TrySetClipboardData(text)
            ? entries.Count == 1 ? $"Copied name: {entries[0].Name}" : $"Copied {entries.Count} names"
            : "Clipboard is not available.");
    }

    private void CopySelectedPathToClipboard()
    {
        var entries = GetSelectedEntries(excludeParent: false);
        if (entries.Count == 0)
        {
            _controller.SetStatus("Nothing selected to copy.");
            return;
        }

        var text = string.Join('\n', entries.Select(GetDisplayPath));
        _controller.SetStatus(_app.Clipboard.TrySetClipboardData(text)
            ? entries.Count == 1 ? $"Copied path: {GetDisplayPath(entries[0])}" : $"Copied {entries.Count} paths"
            : "Clipboard is not available.");
    }

    private void ShowPropertiesDialog()
    {
        var entry = _controller.GetEntry(_listView.SelectedItem ?? -1);
        if (entry is null || entry.Name == "..")
        {
            _controller.SetStatus("Nothing selected to show properties for.");
            return;
        }

        if (entry.IsArchiveEntry)
        {
            _controller.SetStatus("Windows properties are not available for entries inside an archive.");
            return;
        }

        try
        {
            if (!NativeMethods.SHObjectProperties(IntPtr.Zero, NativeMethods.SHOP_FILEPATH, entry.FullPath, null))
            {
                _controller.SetStatus($"Could not open properties for: {entry.Name}");
            }
        }
        catch (Exception ex)
        {
            _controller.SetStatus($"Properties failed: {ex.Message}");
            Log.Warning(ex, "Failed to show properties for {Path}", entry.FullPath);
        }
    }

    private void CopySelectedItemToClipboard()
    {
        var entries = GetSelectedEntries(excludeParent: true);
        if (_controller.IsFlattened)
        {
            entries = CollapseNestedSelections(entries);
        }

        if (entries.Count == 0)
        {
            _controller.SetStatus("Nothing selected to copy.");
            return;
        }

        IReadOnlyList<string> paths;
        if (entries[0].IsArchiveEntry)
        {
            var extraction = RunBackgroundOperation(
                "Copying from ZIP Archive",
                "Extracting selected items…",
                () => _zipArchiveService.ExtractEntries(entries),
                out var failure);

            if (extraction is null)
            {
                _controller.SetStatus($"Copy failed: {failure}");
                return;
            }

            if (extraction.Paths.Count == 0)
            {
                _controller.SetStatus($"Copy failed: {extraction.Errors.FirstOrDefault() ?? "No items were extracted."}");
                return;
            }

            paths = extraction.Paths;
        }
        else
        {
            paths = entries.Select(e => e.FullPath).ToArray();
        }

        _controller.SetStatus(WindowsFileClipboard.TrySetFiles(paths)
            ? entries.Count == 1 ? $"Copied: {entries[0].Name}" : $"Copied {entries.Count} items"
            : "Clipboard is not available.");
    }

    private void ActivateSelectedEntry()
    {
        var index = _listView.SelectedItem ?? -1;
        var entry = _controller.GetEntry(index);
        if (TryOpenZipArchive(entry))
        {
            return;
        }

        if (entry is { IsArchiveEntry: true, IsDirectory: false })
        {
            var extraction = RunBackgroundOperation(
                "Opening ZIP Entry",
                $"Extracting {entry.Name}…",
                () => _zipArchiveService.ExtractEntries([entry]),
                out var failure);

            if (extraction is null || extraction.Paths.Count == 0)
            {
                _controller.SetStatus($"Open failed: {failure ?? extraction?.Errors.FirstOrDefault() ?? "Entry could not be extracted."}");
                return;
            }

            var error = _fileLauncher.Open(extraction.Paths[0]);
            _controller.SetStatus(error ?? $"Opened: {entry.Name}");
            return;
        }

        _controller.Activate(index);
    }

    private void DrillIntoSelectedEntry()
    {
        var index = _listView.SelectedItem ?? -1;
        var entry = _controller.GetEntry(index);
        if (!TryOpenZipArchive(entry))
        {
            _controller.DrillInto(index);
        }
    }

    private bool TryOpenZipArchive(FileSystemEntry? entry)
    {
        if (entry is null
            || entry.IsArchiveEntry
            || entry.IsDirectory
            || !string.Equals(Path.GetExtension(entry.Name), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var listing = RunBackgroundOperation(
            "Opening ZIP Archive",
            $"Indexing {entry.Name}…",
            () => _zipArchiveService.LoadDirectory(entry.FullPath, string.Empty),
            out var failure);

        if (listing is null || listing.Error is not null)
        {
            _controller.SetStatus($"Open failed: {failure ?? listing?.Error ?? "Archive could not be read."}");
            return true;
        }

        _controller.EnterArchive(entry.FullPath);
        return true;
    }

    private static string GetDisplayPath(FileSystemEntry entry) =>
        entry.IsArchiveEntry
            ? $"{entry.FullPath}::{entry.ArchiveEntryPath}"
            : entry.FullPath;

    // Returns every marked entry (Ctrl+B marking mode), falling back to just the current
    // selection when nothing is marked. The ".." parent-directory row is optionally excluded
    // since it can never be a meaningful copy source/destination.
    private IReadOnlyList<FileSystemEntry> GetSelectedEntries(bool excludeParent)
    {
        var source = _listView.Source;
        if (source is not null)
        {
            List<FileSystemEntry>? marked = null;
            for (var i = 0; i < source.Count; i++)
            {
                if (!source.IsMarked(i))
                {
                    continue;
                }

                var markedEntry = _controller.GetEntry(i);
                if (markedEntry is null || (excludeParent && markedEntry.Name == ".."))
                {
                    continue;
                }

                (marked ??= []).Add(markedEntry);
            }

            if (marked is { Count: > 0 })
            {
                return marked;
            }
        }

        var entry = _controller.GetEntry(_listView.SelectedItem ?? -1);
        return entry is not null && (!excludeParent || entry.Name != "..")
            ? [entry]
            : [];
    }

    private void CreateZipArchive()
    {
        if (_controller.IsArchive)
        {
            _controller.SetStatus("Creating nested ZIP archives from archive entries is not supported.");
            return;
        }

        var entries = GetSelectedEntries(excludeParent: true);
        if (_controller.IsFlattened)
        {
            entries = CollapseNestedSelections(entries);
        }

        if (entries.Count == 0)
        {
            _controller.SetStatus("Nothing selected to archive.");
            return;
        }

        var destinationDirectory = _controller.CurrentDirectory;
        var archiveFileName = PromptForZipArchiveName(entries[0].Name, destinationDirectory);
        if (archiveFileName is null)
        {
            return;
        }

        var statusLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = "Preparing archive…",
        };

        var progressLabel = new Label
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill(1),
            Text = "[                                ]   0%",
        };

        var dialog = new Dialog
        {
            Title = "Creating ZIP Archive",
            Width = Dim.Percent(60),
            Height = 7,
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Esc or KeyCode.Enter)
            {
                key.Handled = true;
            }
        };
        dialog.Add(statusLabel, progressLabel);

        ZipArchiveResult? result = null;
        string? failure = null;
        var progress = new Progress<ZipArchiveProgress>(update => _app.Invoke(() =>
        {
            var percent = update.TotalFiles == 0 ? 100 : update.FilesProcessed * 100 / update.TotalFiles;
            const int barWidth = 32;
            var filled = percent * barWidth / 100;
            progressLabel.Text = $"[{new string('#', filled)}{new string('-', barWidth - filled)}] {percent,3}%  {update.FilesProcessed}/{update.TotalFiles} files";
            statusLabel.Text = update.FilesProcessed == update.TotalFiles
                ? "Finalizing archive…"
                : "Compressing files…";
            dialog.SetNeedsDraw();
        }));

        _ = Task.Run(() => _zipArchiveService.Create(
                entries.ToArray(),
                destinationDirectory,
                archiveFileName,
                progress))
            .ContinueWith(task =>
            {
                _app.Invoke(() =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        result = task.Result;
                    }
                    else
                    {
                        failure = task.Exception?.GetBaseException().Message ?? "An unexpected error occurred.";
                        Log.Error(task.Exception, "ZIP archive creation failed unexpectedly");
                    }

                    _app.RequestStop(dialog);
                });
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        RunDialog(dialog);

        if (failure is not null)
        {
            _controller.SetStatus($"Archive failed: {failure}");
            return;
        }

        if (result is null || result.ArchivePath is null)
        {
            var error = result?.Errors.FirstOrDefault() ?? "No files could be archived.";
            _controller.SetStatus($"Archive failed: {error}");
            return;
        }

        var archiveEntry = CreateEntryFromDisk(result.ArchivePath, isDirectory: false);
        _undoHistory.RecordCreatedFile(
            archiveEntry.FullPath,
            $"create {archiveEntry.Name}");
        RefreshAfterMutation(
            () => _controller.AddFlattenedEntry(archiveEntry),
            _controller.RefreshFromDisk);

        _controller.SetStatus(result.Errors.Count == 0
            ? $"Created: {Path.GetFileName(result.ArchivePath)} ({result.FilesAdded} files)"
            : $"Created {Path.GetFileName(result.ArchivePath)}: {result.FilesAdded} files, {result.Errors.Count} skipped.");
    }

    private string? PromptForZipArchiveName(string firstEntryName, string destinationDirectory)
    {
        string? archiveFileName = null;
        var baseName = Path.GetFileNameWithoutExtension(firstEntryName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Archive";
        }

        var textField = new TextField
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = $"{baseName}.zip",
        };

        var dialog = new Dialog
        {
            Title = "ZIP Archive Name",
            Width = Dim.Percent(70),
            Height = 7,
        };

        textField.Accepting += (_, e) =>
        {
            archiveFileName = textField.Text ?? string.Empty;
            e.Handled = true;
            _app.RequestStop();
        };

        dialog.Add(textField);
        textField.SetFocus();
        RunDialog(dialog);

        if (archiveFileName is null)
        {
            return null;
        }

        archiveFileName = archiveFileName.Trim();
        if (archiveFileName.Length == 0)
        {
            _controller.SetStatus("Archive creation cancelled: name cannot be empty.");
            return null;
        }

        if (!archiveFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            archiveFileName += ".zip";
        }

        if (!TryValidateWindowsEntryName(archiveFileName, out var validationError))
        {
            _controller.SetStatus($"Archive creation cancelled: {validationError}");
            return null;
        }

        var archivePath = Path.Combine(destinationDirectory, archiveFileName);
        if (File.Exists(archivePath) || Directory.Exists(archivePath))
        {
            _controller.SetStatus($"Archive creation cancelled: '{archiveFileName}' already exists.");
            return null;
        }

        return archiveFileName;
    }

    private void PasteFromClipboard()
    {
        if (!WindowsFileClipboard.TryGetFiles(out var sources))
        {
            PasteVirtualFilesFromClipboard();
            return;
        }

        if (_controller.IsArchive)
        {
            PasteIntoArchive(sources);
            return;
        }

        // Collect the top-level names that already exist in the destination directory.
        var conflicts = sources
            .Select(s =>
            {
                var n = Path.GetFileName(s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.IsNullOrEmpty(n) ? s : n;
            })
            .Where(n => File.Exists(Path.Combine(_controller.CurrentDirectory, n))
                     || Directory.Exists(Path.Combine(_controller.CurrentDirectory, n)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var conflictChoice = ConflictChoice.None;
        if (conflicts.Count > 0)
        {
            conflictChoice = ShowPasteConflictDialog(conflicts);
            if (conflictChoice == ConflictChoice.None)
            {
                _controller.SetStatus("Paste cancelled.");
                return;
            }
        }

        var resolution = conflictChoice switch
        {
            ConflictChoice.Replace => PasteConflictResolution.Replace,
            ConflictChoice.Duplicate => PasteConflictResolution.Duplicate,
            _ => PasteConflictResolution.None,
        };
        var destinationDirectory = _controller.CurrentDirectory;
        var result = RunPasteOperation(
            "Pasting Files",
            (progress, cancellationToken) => _directoryService.PasteAsync(
                sources,
                destinationDirectory,
                resolution,
                progress,
                cancellationToken),
            out var failure);

        if (result is null)
        {
            _controller.SetStatus($"Paste failed: {failure}");
            return;
        }

        if (result.ItemsChanged)
        {
            RecordPasteUndo(result);
            RefreshAfterMutation(
                updateFlattenedView: StartFlattenOperation,
                updateDirectoryView: () => _controller.EnterDirectory(_controller.CurrentDirectory));
        }

        _controller.SetStatus(FormatPasteResult(result));
    }

    private void PasteVirtualFilesFromClipboard()
    {
        if (!WindowsFileClipboard.TryGetVirtualFileManifest(out var manifest, out var manifestError)
            || manifest is null)
        {
            _controller.SetStatus(manifestError is null
                ? "No files on clipboard to paste."
                : $"Paste failed: {manifestError}");
            return;
        }

        if (_controller.IsArchive)
        {
            _controller.SetStatus("Remote clipboard files cannot be pasted directly into a ZIP archive.");
            return;
        }

        var conflicts = manifest.TopLevelNames
            .Where(name => File.Exists(Path.Combine(_controller.CurrentDirectory, name))
                           || Directory.Exists(Path.Combine(_controller.CurrentDirectory, name)))
            .ToList();

        var conflictChoice = ConflictChoice.None;
        if (conflicts.Count > 0)
        {
            conflictChoice = ShowPasteConflictDialog(conflicts);
            if (conflictChoice == ConflictChoice.None)
            {
                _controller.SetStatus("Paste cancelled.");
                return;
            }
        }

        var resolution = conflictChoice switch
        {
            ConflictChoice.Replace => PasteConflictResolution.Replace,
            ConflictChoice.Duplicate => PasteConflictResolution.Duplicate,
            _ => PasteConflictResolution.None,
        };
        var destinationDirectory = _controller.CurrentDirectory;
        var result = RunPasteOperation(
            "Pasting Remote Files",
            (progress, cancellationToken) => PasteVirtualFilesAsync(
                manifest,
                destinationDirectory,
                resolution,
                progress,
                cancellationToken),
            out var failure);

        if (result is null)
        {
            _controller.SetStatus($"Paste failed: {failure}");
            return;
        }

        if (result.ItemsChanged)
        {
            RecordPasteUndo(result);
            RefreshAfterMutation(
                updateFlattenedView: StartFlattenOperation,
                updateDirectoryView: () => _controller.EnterDirectory(_controller.CurrentDirectory));
        }

        _controller.SetStatus(FormatPasteResult(result));
    }

    private async Task<PasteResult> PasteVirtualFilesAsync(
        VirtualFileManifest manifest,
        string destinationDirectory,
        PasteConflictResolution resolution,
        IProgress<PasteProgress> progress,
        CancellationToken cancellationToken)
    {
        var materialized = await Task.Run(
            () => WindowsFileClipboard.MaterializeVirtualFiles(
                manifest,
                progress,
                cancellationToken),
            CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (materialized.Cancelled)
            {
                return new PasteResult(
                    0,
                    materialized.TotalFiles,
                    0,
                    materialized.BytesReceived,
                    materialized.TotalBytes,
                    true,
                    materialized.Errors);
            }

            if (materialized.SourcePaths.Count == 0)
            {
                return new PasteResult(
                    0,
                    materialized.TotalFiles,
                    0,
                    materialized.BytesReceived,
                    materialized.TotalBytes,
                    false,
                    materialized.Errors.Count > 0
                        ? materialized.Errors
                        : ["No remote clipboard files were received."]);
            }

            var pasteResult = await _directoryService.PasteAsync(
                materialized.SourcePaths,
                destinationDirectory,
                resolution,
                progress,
                cancellationToken).ConfigureAwait(false);

            return materialized.Errors.Count == 0
                ? pasteResult
                : pasteResult with
                {
                    Errors = materialized.Errors.Concat(pasteResult.Errors).ToArray(),
                };
        }
        finally
        {
            WindowsFileClipboard.TryDeleteDirectory(materialized.StagingDirectory);
        }
    }

    private void PasteIntoArchive(IReadOnlyList<string> sources)
    {
        var location = _controller.CurrentLocation;
        var listing = _zipArchiveService.LoadDirectory(
            location.PhysicalPath,
            location.ArchiveDirectory!);
        if (listing.Error is not null)
        {
            _controller.SetStatus($"Paste failed: {listing.Error}");
            return;
        }

        var existingNames = listing.Entries
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = sources
            .Select(source => Path.GetFileName(source.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)))
            .Where(name => name.Length > 0 && existingNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var choice = ConflictChoice.Replace;
        if (conflicts.Count > 0)
        {
            choice = ShowPasteConflictDialog(conflicts);
            if (choice == ConflictChoice.None)
            {
                _controller.SetStatus("Paste cancelled.");
                return;
            }
        }

        var result = RunPasteOperation(
            "Pasting into ZIP Archive",
            (progress, cancellationToken) => Task.Run(() =>
                {
                    try
                    {
                        return _zipArchiveService.AddEntries(
                            location.PhysicalPath,
                            location.ArchiveDirectory!,
                            sources,
                            choice == ConflictChoice.Duplicate
                                ? ZipConflictResolution.Duplicate
                                : ZipConflictResolution.Replace,
                            progress,
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return new ZipMutationResult(0, [], true);
                    }
                },
                CancellationToken.None),
            out var failure);

        if (result?.Cancelled == true)
        {
            _controller.SetStatus("Paste cancelled; the ZIP archive was not changed.");
            return;
        }

        if (result is null || result.ItemsChanged == 0)
        {
            _controller.SetStatus($"Paste failed: {failure ?? result?.Errors.FirstOrDefault() ?? "No items were added."}");
            return;
        }

        _undoHistory.RecordUnavailable(
            "The last ZIP archive change cannot be undone without retaining a backup.");
        var firstName = Path.GetFileName(sources[0].TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        _controller.ReloadSelectingEntry(firstName);
        _controller.SetStatus(result.Errors.Count == 0
            ? $"Added {result.ItemsChanged} {(result.ItemsChanged == 1 ? "item" : "items")} to archive."
            : $"Added {result.ItemsChanged} items with {result.Errors.Count} skipped.");
    }

    private T? RunPasteOperation<T>(
        string title,
        Func<IProgress<PasteProgress>, CancellationToken, Task<T>> operation,
        out string? failure)
        where T : class
    {
        T? result = null;
        string? operationFailure = null;
        var cancellationRequested = false;
        var dialogClosed = false;
        using var cancellation = new CancellationTokenSource();

        var statusLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = "Preparing paste…",
        };
        var currentItemLabel = new Label
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Text = string.Empty,
        };
        var progressBar = new ProgressBar
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Height = 1,
            Fraction = 0,
        };
        var detailLabel = new Label
        {
            X = 1,
            Y = 5,
            Width = Dim.Fill(1),
            Text = "Scanning sources…",
        };
        var cancelButton = new Button
        {
            X = Pos.Center(),
            Y = 7,
            Text = "Cancel",
        };
        var dialog = new Dialog
        {
            Title = title,
            Width = Dim.Percent(70),
            Height = 11,
        };

        void requestCancellation()
        {
            if (cancellationRequested)
            {
                return;
            }

            cancellationRequested = true;
            cancellation.Cancel();
            cancelButton.Enabled = false;
            statusLabel.Text = "Cancelling…";
            dialog.SetNeedsDraw();
        }

        cancelButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            requestCancellation();
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                requestCancellation();
            }
        };
        dialog.Add(statusLabel, currentItemLabel, progressBar, detailLabel, cancelButton);

        var progress = new Progress<PasteProgress>(update => _app.Invoke(() =>
        {
            if (dialogClosed)
            {
                return;
            }

            if (!cancellationRequested)
            {
                statusLabel.Text = update.Phase switch
                {
                    PasteProgressPhase.Preparing => "Preparing paste…",
                    PasteProgressPhase.Receiving => "Receiving remote files…",
                    PasteProgressPhase.Rebuilding => "Rebuilding archive…",
                    PasteProgressPhase.Finalizing => "Finalizing…",
                    _ => "Pasting files…",
                };
            }

            currentItemLabel.Text = update.CurrentPath is null
                ? string.Empty
                : Path.GetFileName(update.CurrentPath);
            progressBar.Fraction = GetPasteFraction(update);
            detailLabel.Text = update.Phase switch
            {
                PasteProgressPhase.Preparing when update.TotalBytes > 0 =>
                    $"Found {update.TotalFiles:N0} files ({FormatByteCount(update.TotalBytes)})",
                PasteProgressPhase.Preparing =>
                    $"Found {update.TotalFiles:N0} files",
                PasteProgressPhase.Rebuilding =>
                    "The original archive remains unchanged until this completes.",
                PasteProgressPhase.Finalizing =>
                    $"{update.FilesCompleted:N0}/{update.TotalFiles:N0} files completed",
                _ when update.TotalBytes > 0 =>
                    $"{update.FilesCompleted:N0}/{update.TotalFiles:N0} files  |  "
                    + $"{FormatByteCount(update.BytesCopied)} / {FormatByteCount(update.TotalBytes)}",
                _ => $"{update.FilesCompleted:N0}/{update.TotalFiles:N0} files",
            };
            dialog.SetNeedsDraw();
        }));

        Task<T> task;
        try
        {
            task = operation(progress, cancellation.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Operation} failed to start", title);
            failure = ex.Message;
            return null;
        }

        _ = task.ContinueWith(completedTask =>
        {
            _app.Invoke(() =>
            {
                dialogClosed = true;
                if (completedTask.IsCompletedSuccessfully)
                {
                    result = completedTask.Result;
                }
                else if (!completedTask.IsCanceled)
                {
                    operationFailure = completedTask.Exception?.GetBaseException().Message
                        ?? "An unexpected error occurred.";
                    Log.Error(completedTask.Exception, "{Operation} failed unexpectedly", title);
                }

                _app.RequestStop(dialog);
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        cancelButton.SetFocus();
        RunDialog(dialog);
        failure = operationFailure;
        return result;
    }

    private static float GetPasteFraction(PasteProgress progress)
    {
        if (progress.Phase == PasteProgressPhase.Finalizing)
        {
            return 1;
        }

        if (progress.TotalBytes > 0)
        {
            return (float)Math.Clamp(
                (double)progress.BytesCopied / progress.TotalBytes,
                0,
                1);
        }

        return progress.TotalFiles > 0
            ? (float)Math.Clamp((double)progress.FilesCompleted / progress.TotalFiles, 0, 1)
            : 0;
    }

    private static string FormatPasteResult(PasteResult result)
    {
        if (!result.Cancelled && !result.ItemsChanged && result.Errors.Count > 0)
        {
            return $"Paste failed: {result.Errors[0]}";
        }

        var summary = result.Cancelled
            ? $"Paste cancelled after {result.FilesCopied:N0} files and {result.DirectoriesCreated:N0} folders"
            : $"Pasted {result.FilesCopied:N0} files and {result.DirectoriesCreated:N0} folders";

        return result.Errors.Count == 0
            ? summary
            : $"{summary}; {result.Errors.Count:N0} skipped — {result.Errors[0]}";
    }

    private void RecordPasteUndo(PasteResult result)
    {
        if (result.ReplacedExisting)
        {
            _undoHistory.RecordUnavailable(
                "The last replacement paste cannot be undone because replaced data is not retained.");
            return;
        }

        if (!result.UndoTrackingComplete)
        {
            _undoHistory.RecordUnavailable(
                "The last paste is too large to undo within the session history limit.");
            return;
        }

        _undoHistory.RecordPaste(result);
    }

    private void UndoLastOperation()
    {
        if (!_undoHistory.HasEntries)
        {
            _controller.SetStatus("Nothing to undo.");
            return;
        }

        var result = RunBackgroundOperation(
            "Undo",
            "Undoing the last file operation…",
            _undoHistory.Undo,
            out var failure);
        if (result is null)
        {
            _controller.SetStatus($"Undo failed: {failure ?? "An unexpected error occurred."}");
            return;
        }

        if (result.RemovedPaths.Count > 0 || result.MovedFromPath is not null)
        {
            RefreshAfterUndo(result);
        }

        _controller.SetStatus(result.Message);
    }

    private void RefreshAfterUndo(UndoResult result)
    {
        try
        {
            var location = _controller.CurrentLocation;
            if (result.MovedFromPath is not null
                && result.MovedToPath is not null
                && IsSameOrDescendant(location.PhysicalPath, result.MovedFromPath))
            {
                var suffix = Path.GetFullPath(location.PhysicalPath)
                    [Path.GetFullPath(result.MovedFromPath).Length..];
                var movedLocationPath = Path.GetFullPath(result.MovedToPath) + suffix;
                if (location.IsArchive)
                {
                    _controller.EnterArchive(movedLocationPath, location.ArchiveDirectory!);
                }
                else
                {
                    _controller.EnterDirectory(movedLocationPath);
                }

                return;
            }

            foreach (var removedPath in result.RemovedPaths)
            {
                if (!IsSameOrDescendant(location.PhysicalPath, removedPath))
                {
                    continue;
                }

                var parent = Path.GetDirectoryName(Path.GetFullPath(removedPath))
                    ?? Environment.CurrentDirectory;
                _controller.EnterDirectory(parent);
                return;
            }

            RefreshAfterMutation(
                updateFlattenedView: StartFlattenOperation,
                updateDirectoryView: _controller.RefreshFromDisk);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            Log.Warning(ex, "Failed to refresh the view after undo");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected failure refreshing the view after undo");
        }
    }

    private static bool IsSameOrDescendant(string path, string possibleParent)
    {
        var fullPath = Path.GetFullPath(path);
        var fullParent = Path.GetFullPath(possibleParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullParent, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(
                   $"{fullParent}{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    // Terminal.Gui sets Console.Title to the dialog Title on every Run() call.
    // Wrap all modal dialog runs through here to keep the title static.
    private void RunDialog(Dialog dialog)
    {
        var savedTitle = Console.Title;
        _app.Run(dialog);
        Console.Title = savedTitle;
    }

    private T? RunBackgroundOperation<T>(
        string title,
        string message,
        Func<T> operation,
        out string? failure)
        where T : class
    {
        T? result = null;
        string? operationFailure = null;

        var label = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = message,
        };

        var dialog = new Dialog
        {
            Title = title,
            Width = Dim.Percent(60),
            Height = 5,
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Esc or KeyCode.Enter)
            {
                key.Handled = true;
            }
        };
        dialog.Add(label);

        _ = Task.Run(operation).ContinueWith(task =>
        {
            _app.Invoke(() =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    result = task.Result;
                }
                else
                {
                    operationFailure = task.Exception?.GetBaseException().Message
                        ?? "An unexpected error occurred.";
                    Log.Error(task.Exception, "{Operation} failed unexpectedly", title);
                }

                _app.RequestStop(dialog);
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        RunDialog(dialog);
        failure = operationFailure;
        return result;
    }

    private ConflictChoice ShowPasteConflictDialog(IReadOnlyList<string> conflicts)
    {
        var choice = ConflictChoice.None;

        var message = conflicts.Count == 1
            ? $"\"{conflicts[0]}\" already exists in this directory."
            : $"{conflicts.Count} items already exist in this directory.";

        var messageLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = message,
        };

        var hintLabel = new Label
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Text = "[R] replace existing   [D] duplicate   Esc cancel",
        };

        var dialog = new Dialog
        {
            Title = "File Conflict",
            Width = Dim.Percent(65),
            Height = 7,
        };

        // Mask out modifier bits and normalize to uppercase so both r/R and d/D are accepted.
        dialog.KeyDown += (_, k) =>
        {
            var ch = char.ToUpperInvariant((char)((uint)k.KeyCode & 0xFFFF));
            if (ch == 'R')
            {
                choice = ConflictChoice.Replace;
                k.Handled = true;
                _app.RequestStop();
            }
            else if (ch == 'D')
            {
                choice = ConflictChoice.Duplicate;
                k.Handled = true;
                _app.RequestStop();
            }
        };

        dialog.Add(messageLabel, hintLabel);
        RunDialog(dialog);

        return choice;
    }

    private void AddCurrentDirectoryToFavorites()
    {
        if (_controller.IsArchive)
        {
            _controller.SetStatus("Archive locations cannot be added to favorites.");
            return;
        }

        var directory = _controller.CurrentDirectory;

        // Fire-and-forget: keep the UI responsive; status is updated when the task completes.
        _ = _favoritesService.AddAsync(directory).ContinueWith(
            t => _controller.SetStatus(t.Result switch
            {
                AddFavoriteResult.Added         => $"Added to favorites: {directory}",
                AddFavoriteResult.AlreadyExists => $"Already in favorites: {directory}",
                AddFavoriteResult.AtCapacity    => $"Favorites list is full ({IFavoritesService.MaxFavorites} max).",
                _                               => "Could not add favorite.",
            }),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    // Handles both Ctrl+O (local, this pane only) and Ctrl+Alt+O (global default, persisted and
    // applied to every pane not currently overriding it locally).
    private void ShowSortDialog(bool global)
    {
        SortMode? chosen = null;

        var hintLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = "[N]ame   [D]ate   [S]ize   Esc cancel",
        };

        var dialog = new Dialog
        {
            Title = global ? "Global Sort Order" : "Sort Order (this tab)",
            Width = Dim.Percent(50),
            Height = 6,
        };

        // Mask out modifier bits and normalize to uppercase so both lower and upper case letters
        // are accepted, matching the paste-conflict dialog's key handling.
        dialog.KeyDown += (_, k) =>
        {
            var ch = char.ToUpperInvariant((char)((uint)k.KeyCode & 0xFFFF));
            SortMode? mode = ch switch
            {
                'N' => SortMode.Name,
                'D' => SortMode.Date,
                'S' => SortMode.Size,
                _ => null,
            };

            if (mode is not null)
            {
                chosen = mode;
                k.Handled = true;
                _app.RequestStop();
            }
        };

        dialog.Add(hintLabel);
        RunDialog(dialog);

        if (chosen is null)
        {
            return;
        }

        if (global)
        {
            _sortSettingsService.SetGlobalSortMode(chosen.Value);
            _controller.SetStatus($"Global sort order set to {chosen.Value}.");
        }
        else
        {
            _controller.SetLocalSortMode(chosen.Value);
            _controller.SetStatus($"Sort order for this tab set to {chosen.Value}.");
        }
    }

    private void ShowDrivesDialog()
    {
        // Only ready drives are listed; the readiness probe is the expensive part (removable/network
        // spin-up) and the size properties below are cheap once a drive answered it.
        var drives = new List<string>();
        var rows = new List<string>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                drives.Add(drive.Name);
                rows.Add(FormatDriveRow(drive));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive can vanish or refuse access between the readiness probe and the size read.
                Log.Warning(ex, "Skipping drive {Drive} while building the drive picker", drive.Name);
            }
        }

        if (drives.Count == 0)
        {
            _controller.SetStatus("No drives are available.");
            return;
        }

        string? chosen = null;

        var listView = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
        };
        listView.SetSource(new ObservableCollection<string>(rows));

        var dialog = new Dialog
        {
            Title = "Drives",
            Width = Dim.Percent(50),
            Height = Dim.Percent(60),
        };

        // Confirm via Enter (ListView's Accept command) or Ctrl+Alt+L. Both paths share the same
        // accept action so the selection logic is never duplicated. Cancel is left to the Dialog's
        // built-in Esc handling.
        void acceptDrive()
        {
            chosen = drives[listView.SelectedItem ?? 0];
            _app.RequestStop();
        }

        listView.Accepting += (_, e) => { acceptDrive(); e.Handled = true; };
        AttachVimNavigation(listView, drives.Count, acceptDrive);

        dialog.Add(listView);
        listView.SetFocus();

        RunDialog(dialog);

        if (chosen is not null && Directory.Exists(chosen))
        {
            _controller.EnterDirectory(chosen);
        }
        else if (chosen is not null)
        {
            _controller.SetStatus($"Drive is not accessible: {chosen}");
        }
    }

    private static string FormatDriveRow(DriveInfo drive)
    {
        var total = drive.TotalSize;
        var free = drive.TotalFreeSpace;
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.DriveType.ToString() : drive.VolumeLabel;

        if (total <= 0)
        {
            return $"{drive.Name,-4} {label,-16}";
        }

        const double GB = 1024d * 1024d * 1024d;
        var percentFree = free * 100d / total;

        return $"{drive.Name,-4} {label,-16} {free / GB,8:0.0} GB free of {total / GB,8:0.0} GB ({percentFree,3:0}% free)";
    }

    private void ShowFavoritesDialog()
    {
        var favorites = _favoritesService.Favorites.ToList();

        if (favorites.Count == 0)
        {
            _controller.SetStatus("No favorites saved yet. Use Ctrl+Alt+F to add the current directory.");
            return;
        }

        string? chosen = null;

        static ObservableCollection<string> BuildRows(List<string> list) =>
            new(list.Select((f, i) => $"{i + 1}  {f}"));

        var listView = new FilterListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
        };
        listView.SetSource(BuildRows(favorites));

        var dialog = new Dialog
        {
            Title = "Favorites  (1-9 navigate · Del remove)",
            Width = Dim.Percent(70),
            Height = Dim.Percent(60),
        };

        // Both Enter (via Accepting) and Ctrl+Alt+L share the same accept action.
        void acceptFavorite()
        {
            var index = listView.SelectedItem ?? 0;
            if (index >= 0 && index < favorites.Count)
            {
                chosen = favorites[index];
                _app.RequestStop();
            }
        }

        // FilterListView routes printable characters here before ListView's type-ahead can move
        // the selection, so 1-9 immediately enter the corresponding favorite.
        listView.CharacterTyped += character =>
        {
            if (character >= '1' && character <= '9')
            {
                var index = character - '1';
                if (index < favorites.Count)
                {
                    chosen = favorites[index];
                    _app.RequestStop();
                }
            }
        };

        listView.KeyDown += async (_, key) =>
        {
            // Delete: remove the selected favorite and refresh the numbered list.
            if (key.KeyCode == KeyCode.Delete)
            {
                var index = listView.SelectedItem ?? 0;
                if (index >= 0 && index < favorites.Count)
                {
                    var path = favorites[index];
                    if (await _favoritesService.RemoveAsync(path).ConfigureAwait(true))
                    {
                        favorites.RemoveAt(index);

                        if (favorites.Count == 0)
                        {
                            _app.RequestStop();
                            return;
                        }

                        listView.SetSource(BuildRows(favorites));
                        listView.SelectedItem = Math.Min(index, favorites.Count - 1);
                        listView.EnsureSelectedItemVisible();
                    }

                    key.Handled = true;
                }
                return;
            }

            // Ctrl+Alt navigation: I=up, K=down, L=accept.
            if (!key.IsCtrl || !key.IsAlt)
                return;

            var baseKey = key.KeyCode & ~(KeyCode.CtrlMask | KeyCode.AltMask);

            if (baseKey == KeyCode.L)
            {
                acceptFavorite();
                key.Handled = true;
                return;
            }

            int delta = baseKey switch { KeyCode.I => -1, KeyCode.K => 1, _ => 0 };
            if (delta == 0 || favorites.Count == 0)
                return;

            var current = listView.SelectedItem ?? 0;
            var next = Math.Clamp(current + delta, 0, favorites.Count - 1);
            if (next == current)
                return;

            listView.SelectedItem = next;
            listView.EnsureSelectedItemVisible();
            key.Handled = true;
        };

        listView.Accepting += (_, e) => { acceptFavorite(); e.Handled = true; };

        dialog.Add(listView);
        listView.SetFocus();

        RunDialog(dialog);

        if (chosen is not null && Directory.Exists(chosen))
        {
            _controller.EnterDirectory(chosen);
        }
        else if (chosen is not null)
        {
            _controller.SetStatus($"Directory no longer exists: {chosen}");
        }
    }

    private void ShowDeleteConfirmDialog()
    {
        var entries = GetSelectedEntries(excludeParent: true);
        if (_controller.IsFlattened)
        {
            entries = CollapseNestedSelections(entries);
        }

        if (entries.Count == 0)
        {
            _controller.SetStatus("Nothing selected to delete.");
            return;
        }

        bool confirmed = false;

        var label = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = entries.Count == 1
                ? $"Delete \"{entries[0].Name}\"? Press Enter to confirm."
                : $"Are you sure you want to delete all {entries.Count} selected items? Press Enter to confirm.",
        };

        var dialog = new Dialog
        {
            Title = "Confirm Delete",
            Width = Dim.Percent(60),
            Height = 6,
        };

        // Confirm via Enter on the dialog's KeyDown; cancel via the Dialog's built-in
        // Esc handling.
        dialog.KeyDown += (_, k) =>
        {
            if (k.KeyCode == KeyCode.Enter)
            {
                confirmed = true;
                k.Handled = true;
                _app.RequestStop();
            }
        };

        dialog.Add(label);

        RunDialog(dialog);

        if (!confirmed)
        {
            return;
        }

        if (_controller.IsArchive)
        {
            var result = RunBackgroundOperation(
                "Updating ZIP Archive",
                "Deleting items and rebuilding archive…",
                () => _zipArchiveService.DeleteEntries(entries),
                out var failure);

            if (result is null || result.ItemsChanged == 0)
            {
                _controller.SetStatus($"Delete failed: {failure ?? result?.Errors.FirstOrDefault() ?? "No items were deleted."}");
                return;
            }

            _undoHistory.RecordUnavailable(
                "The last ZIP archive change cannot be undone without retaining a backup.");
            var location = _controller.CurrentLocation;
            _controller.EnterArchive(location.PhysicalPath, location.ArchiveDirectory!);
            _controller.SetStatus(result.Errors.Count == 0
                ? result.ItemsChanged == 1 ? $"Deleted: {entries[0].Name}" : $"Deleted {result.ItemsChanged} items"
                : $"Deleted {result.ItemsChanged} items with {result.Errors.Count} errors.");
            return;
        }

        var deleted = 0;
        var deletedEntries = new List<FileSystemEntry>(entries.Count);
        string? firstFailure = null;
        var failed = 0;

        foreach (var entry in entries)
        {
            var skipReason = GetDeleteBlockReason(entry);
            if (skipReason is not null)
            {
                failed++;
                firstFailure ??= $"{entry.Name}: {skipReason}";
                Log.Warning("Skipped delete of {Path}: {Reason}", entry.FullPath, skipReason);
                continue;
            }

            try
            {
                if (entry.IsDirectory)
                {
                    Directory.Delete(entry.FullPath, recursive: true);
                }
                else
                {
                    File.Delete(entry.FullPath);
                }

                deleted++;
                deletedEntries.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                firstFailure ??= $"{entry.Name}: {ex.Message}";
                Log.Warning(ex, "Failed to delete {Path}", entry.FullPath);
            }
            catch (Exception ex)
            {
                failed++;
                firstFailure ??= $"{entry.Name}: {ex.Message}";
                Log.Error(ex, "Unexpected failure deleting {Path}", entry.FullPath);
            }
        }

        if (deleted > 0)
        {
            _undoHistory.RecordUnavailable(
                "The last delete cannot be undone because deleted data is not retained.");
            RefreshAfterMutation(
                () => _controller.RemoveFlattenedEntries(deletedEntries),
                () => _controller.EnterDirectory(_controller.CurrentDirectory));
        }

        _controller.SetStatus(failed == 0
            ? deleted == 1 ? $"Deleted: {entries[0].Name}" : $"Deleted {deleted} items"
            : $"Deleted {deleted}, skipped {failed} — {firstFailure}");
    }

    // Cheap pre-check so obvious blockers are reported without attempting a partial delete.
    // A full "is it locked?" check would require opening every file (recursively for
    // directories), which is far too costly for large selections — those failures are
    // instead caught per item during the delete itself.
    private static string? GetDeleteBlockReason(FileSystemEntry entry)
    {
        try
        {
            if (entry.IsDirectory)
            {
                if (!Directory.Exists(entry.FullPath))
                {
                    return "no longer exists";
                }
            }
            else
            {
                if (!File.Exists(entry.FullPath))
                {
                    return "no longer exists";
                }

                if ((File.GetAttributes(entry.FullPath) & FileAttributes.ReadOnly) != 0)
                {
                    return "read-only";
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private void ShowRenameDialog()
    {
        var entry = _controller.GetEntry(_listView.SelectedItem ?? -1);
        if (entry is null || entry.Name == "..")
        {
            _controller.SetStatus("Nothing selected to rename.");
            return;
        }

        string? newName = null;

        var textField = new TextField
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = entry.Name,
        };

        var dialog = new Dialog
        {
            Title = $"Rename: {entry.Name}",
            Width = Dim.Percent(70),
            Height = 7,
        };

        // Confirm via the TextField's Accept command (Enter); cancel via the Dialog's
        // built-in Esc handling.
        textField.Accepting += (_, e) =>
        {
            newName = textField.Text ?? string.Empty;
            e.Handled = true;
            _app.RequestStop();
        };

        dialog.Add(textField);
        textField.SetFocus();

        RunDialog(dialog);

        if (newName is null)
        {
            return;
        }

        newName = newName.Trim();

        if (newName.Length == 0)
        {
            _controller.SetStatus("Rename cancelled: name cannot be empty.");
            return;
        }

        if (string.Equals(newName, entry.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (entry.IsArchiveEntry)
        {
            if (!TryValidateWindowsEntryName(newName, out var validationError))
            {
                _controller.SetStatus($"Rename cancelled: {validationError}");
                return;
            }

            var location = _controller.CurrentLocation;
            var listing = _zipArchiveService.LoadDirectory(
                location.PhysicalPath,
                location.ArchiveDirectory!);
            if (listing.Error is not null)
            {
                _controller.SetStatus($"Rename failed: {listing.Error}");
                return;
            }

            if (listing.Entries.Any(existing =>
                    !string.Equals(existing.Identity, entry.Identity, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                _controller.SetStatus($"Rename cancelled: '{newName}' already exists.");
                return;
            }

            var result = RunBackgroundOperation(
                "Updating ZIP Archive",
                "Renaming item and rebuilding archive…",
                () => _zipArchiveService.RenameEntry(entry, newName),
                out var failure);

            if (result is null || result.ItemsChanged == 0)
            {
                _controller.SetStatus($"Rename failed: {failure ?? result?.Errors.FirstOrDefault() ?? "Archive was not changed."}");
                return;
            }

            _undoHistory.RecordUnavailable(
                "The last ZIP archive change cannot be undone without retaining a backup.");
            _controller.ReloadSelectingEntry(newName);
            _controller.SetStatus($"Renamed: {entry.Name} → {newName}");
            return;
        }

        var containingDirectory = Path.GetDirectoryName(entry.FullPath);
        if (containingDirectory is null)
        {
            _controller.SetStatus($"Rename failed: could not determine the parent of {entry.FullPath}");
            return;
        }

        var newFullPath = Path.Combine(containingDirectory, newName);

        try
        {
            if (entry.IsDirectory)
            {
                Directory.Move(entry.FullPath, newFullPath);
            }
            else
            {
                File.Move(entry.FullPath, newFullPath);
            }

            _undoHistory.RecordRename(entry.FullPath, newFullPath, entry.IsDirectory);
            RefreshAfterMutation(
                () => _controller.RenameFlattenedEntry(entry, newFullPath),
                () => _controller.ReloadSelectingEntry(newName));
            _controller.SetStatus($"Renamed: {entry.Name} \u2192 {newName}");
        }
        catch (Exception ex)
        {
            _controller.SetStatus($"Rename failed: {ex.Message}");
            Log.Warning(ex, "Failed to rename {Path} to {NewName}", entry.FullPath, newName);
        }
    }

    private void ShowCreateFileDialog()
    {
        string? name = null;

        var textField = new TextField
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
        };

        var dialog = new Dialog
        {
            Title = "New file or folder (end folders with /)",
            Width = Dim.Percent(70),
            Height = 7,
        };

        textField.Accepting += (_, e) =>
        {
            name = textField.Text ?? string.Empty;
            e.Handled = true;
            _app.RequestStop();
        };

        dialog.Add(textField);
        textField.SetFocus();

        RunDialog(dialog);

        if (name is null)
        {
            return;
        }

        name = name.Trim();
        var createDirectory = name.EndsWith('/');

        if (createDirectory)
        {
            name = name[..^1];
        }

        if (name.Length == 0)
        {
            _controller.SetStatus("Create cancelled: name cannot be empty.");
            return;
        }

        if (_controller.IsArchive)
        {
            if (!TryValidateWindowsEntryName(name, out var archiveValidationError))
            {
                _controller.SetStatus($"Create cancelled: {archiveValidationError}");
                return;
            }

            var location = _controller.CurrentLocation;
            var listing = _zipArchiveService.LoadDirectory(
                location.PhysicalPath,
                location.ArchiveDirectory!);
            if (listing.Error is not null)
            {
                _controller.SetStatus($"Create failed: {listing.Error}");
                return;
            }

            if (listing.Entries.Any(existing =>
                    string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                _controller.SetStatus($"Create cancelled: '{name}' already exists.");
                return;
            }

            var result = RunBackgroundOperation(
                "Updating ZIP Archive",
                "Creating item and rebuilding archive…",
                () => _zipArchiveService.CreateEntry(
                    location.PhysicalPath,
                    location.ArchiveDirectory!,
                    name,
                    createDirectory),
                out var failure);

            if (result is null || result.ItemsChanged == 0)
            {
                _controller.SetStatus($"Create failed: {failure ?? result?.Errors.FirstOrDefault() ?? "Archive was not changed."}");
                return;
            }

            _undoHistory.RecordUnavailable(
                "The last ZIP archive change cannot be undone without retaining a backup.");
            _controller.ReloadSelectingEntry(name);
            _controller.SetStatus($"Created {(createDirectory ? "folder" : "file")}: {name}");
            return;
        }

        if (createDirectory && !TryValidateWindowsEntryName(name, out var validationError))
        {
            _controller.SetStatus($"Create cancelled: {validationError}");
            return;
        }

        try
        {
            var fullPath = Path.Combine(_controller.CurrentDirectory, name);

            if (createDirectory)
            {
                if (Directory.Exists(fullPath))
                {
                    _controller.SetStatus($"Create cancelled: folder already exists: {name}");
                    return;
                }

                Directory.CreateDirectory(fullPath);
            }
            else
            {
                // CreateNew fails if the file already exists, so no separate existence check is needed.
                using (new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }
            }

            var createdEntry = CreateEntryFromDisk(fullPath, createDirectory);
            if (createDirectory)
            {
                _undoHistory.RecordCreatedDirectory(fullPath, $"create {name}");
            }
            else
            {
                _undoHistory.RecordCreatedFile(
                    fullPath,
                    $"create {name}");
            }
            RefreshAfterMutation(
                () => _controller.AddFlattenedEntry(createdEntry),
                () => _controller.ReloadSelectingEntry(name));
            _controller.SetStatus($"Created {(createDirectory ? "folder" : "file")}: {name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _controller.SetStatus($"Create failed: {ex.Message}");
            Log.Warning(ex, "Failed to create {EntryType} {Name} in {Directory}",
                createDirectory ? "folder" : "file", name, _controller.CurrentDirectory);
        }
        catch (Exception ex)
        {
            _controller.SetStatus($"Create failed: {ex.Message}");
            Log.Error(ex, "Unexpected failure creating {EntryType} {Name} in {Directory}",
                createDirectory ? "folder" : "file", name, _controller.CurrentDirectory);
        }
    }

    private static bool TryValidateWindowsEntryName(string name, out string error)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "name contains a character that Windows does not allow.";
            return false;
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            error = "name cannot end with a space or period.";
            return false;
        }

        if (name is "." or "..")
        {
            error = "name cannot be '.' or '..'.";
            return false;
        }

        var extensionIndex = name.IndexOf('.');
        var nameWithoutExtension = extensionIndex >= 0 ? name[..extensionIndex] : name;
        if (ReservedWindowsNames.Contains(nameWithoutExtension))
        {
            error = $"'{nameWithoutExtension}' is a reserved Windows name.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void ShowExecuteDialog()
    {
        var entry = _controller.GetEntry(_listView.SelectedItem ?? -1);
        if (entry is null)
        {
            _controller.SetStatus("Nothing selected to execute.");
            return;
        }

        if (entry.IsArchiveEntry)
        {
            _controller.SetStatus("Execute with arguments is not available for archive entries.");
            return;
        }

        string? args = null;

        var textField = new TextField
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
        };

        var dialog = new Dialog
        {
            Title = $"Execute: {entry.Name}",
            Width = Dim.Percent(70),
            Height = 7,
        };

        // See ShowDrivesDialog: confirm via the TextField's Accept command (Enter); cancel via the
        // Dialog's built-in Esc handling.
        textField.Accepting += (_, e) =>
        {
            args = textField.Text ?? string.Empty;
            e.Handled = true;
            _app.RequestStop();
        };

        dialog.Add(textField);
        textField.SetFocus();

        RunDialog(dialog);

        if (args is null)
        {
            return;
        }

        var error = _fileLauncher.Open(entry.FullPath, args);
        _controller.SetStatus(error ?? $"Launched: {entry.Name}");
    }

    private void ShowRunCommandDialog()
    {
        if (_controller.IsArchive)
        {
            _controller.SetStatus("Commands cannot run with an archive directory as their working directory.");
            return;
        }

        string? command = null;

        var textField = new TextField
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
        };

        var dialog = new Dialog
        {
            Title = $"Run in: {_controller.CurrentDirectory}",
            Width = Dim.Percent(70),
            Height = 7,
        };

        // See ShowDrivesDialog: confirm via the TextField's Accept command (Enter); cancel via the
        // Dialog's built-in Esc handling.
        textField.Accepting += (_, e) =>
        {
            command = textField.Text ?? string.Empty;
            e.Handled = true;
            _app.RequestStop();
        };

        dialog.Add(textField);
        textField.SetFocus();

        RunDialog(dialog);

        if (command is null)
        {
            return;
        }

        command = command.Trim();

        if (command.Length == 0)
        {
            _controller.SetStatus("Run cancelled: command cannot be empty.");
            return;
        }

        var error = WindowsCommandRunner.Run(command, _controller.CurrentDirectory);

        _controller.SetStatus(error ?? $"Running: {command}");
    }

    private void ShowMoveToDialog()
    {
        string? input = null;

        var textField = new TextField
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = _controller.CurrentDirectory,
        };

        var dialog = new Dialog
        {
            Title = "Go To Path",
            Width = Dim.Percent(70),
            Height = 7,
        };

        // Confirm via the TextField's Accept command (Enter); cancel via the Dialog's
        // built-in Esc handling.
        textField.Accepting += (_, e) =>
        {
            input = textField.Text ?? string.Empty;
            e.Handled = true;
            _app.RequestStop();
        };

        dialog.Add(textField);
        textField.SetFocus();

        RunDialog(dialog);

        if (input is null)
        {
            return;
        }

        input = input.Trim();

        if (input.Length == 0)
        {
            _controller.SetStatus("Go to cancelled: path cannot be empty.");
            return;
        }

        try
        {
            var fullPath = Path.IsPathRooted(input)
                ? input
                : Path.GetFullPath(input, _controller.CurrentDirectory);

            if (Directory.Exists(fullPath))
            {
                _controller.EnterDirectory(fullPath);
            }
            else if (File.Exists(fullPath))
            {
                var error = _fileLauncher.Open(fullPath);
                _controller.SetStatus(error ?? $"Opened: {Path.GetFileName(fullPath)}");
            }
            else
            {
                _controller.SetStatus($"Path not found: {fullPath}");
            }
        }
        catch (Exception ex)
        {
            _controller.SetStatus($"Go to failed: {ex.Message}");
            Log.Warning(ex, "Failed to go to {Path}", input);
        }
    }

    private void ShowHelpDialog()
    {
        var lines = new ObservableCollection<string>
        {
            "  Navigation",
            "  \u2191 / \u2193           Move selection up / down",
            "  PgUp / PgDn     Page up / page down",
            "  Home / End      Jump to first / last",
            "  \u2192               Drill into directory or ZIP archive",
            "  \u2190               Go to parent directory",
            "  Enter           Open file, directory, or ZIP archive",
            "  Del             Delete selected / marked items",
            "  Backspace       Edit the active filter",
            "  Esc             Clear filter  /  quit",
            "  F1 \u2013 F9         Switch to tab 1 \u2013 9",
            "  (type)          Filter entries live",
            "",
            "  Commands",
            "  Ctrl+C          Copy selected item to clipboard",
            "  Ctrl+V          Paste clipboard here",
            "  Ctrl+N          Copy selected name to clipboard",
            "  Ctrl+P          Copy selected path to clipboard",
            "  Ctrl+R          Rename selected item",
            "  Ctrl+Z          Undo last supported file operation",
            "  Ctrl+B          Toggle marking mode (hint ctrl+A will select all, ctrl+u will unselect all)",
            "  Ctrl+D          Show drive picker",
            "  Ctrl+E          Toggle flattened directory view",
            "  Ctrl+F          Show favorites",
            "  Ctrl+G          Go to path",
            "  Ctrl+O          Set sort order (this tab)",
            "  Ctrl+X          Execute with arguments",
            "  Ctrl+T          Duplicate tab",
            "  Ctrl+W          Close current tab",
            "  Ctrl+Tab        Cycle to next tab",
            "  Ctrl+Q          Quit",
            "  Ctrl+Alt+H      Show this help",
            "",
            "  Ctrl+Alt shortcuts",
            "  Ctrl+Alt+F      Add current directory to favorites",
            "  Ctrl+Alt+C      Create a file or folder here",
            "  Ctrl+Alt+I      Move selection up  (vim-style)",
            "  Ctrl+Alt+K      Move selection down  (vim-style)",
            "  Ctrl+Alt+J      Go to parent  (vim-style)",
            "  Ctrl+Alt+L      Drill into directory  (vim-style)",
            "  Ctrl+Alt+X      Run a command in the current directory",
            "  Ctrl+Alt+P      Show Windows Properties dialog",
            "  Ctrl+Alt+O      Set global sort order",
            "  Ctrl+Alt+Z      Create ZIP archive from selected items",
        };

        var listView = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
        };
        listView.SetSource(lines);
        AttachVimNavigation(listView, lines.Count, _app.RequestStop);

        var dialog = new Dialog
        {
            Title = "Help \u2013 Keyboard Shortcuts",
            Width = Dim.Percent(70),
            Height = Dim.Percent(80),
        };

        listView.Accepting += (_, e) => { e.Handled = true; _app.RequestStop(); };

        dialog.Add(listView);
        listView.SetFocus();

        RunDialog(dialog);
    }

    private void Refresh()
    {
        if (!_controller.IsFlattened)
        {
            StopFlattenOperation();
        }

        var entries = _controller.FilteredEntries;

        // Only rebuild the list source and reset the selection when the entry set actually
        // changed (directory change or filter edit). Status-only updates, such as after a
        // command like Ctrl+C, keep the current selection intact.
        if (!ReferenceEquals(entries, _renderedEntries))
        {
            var selectedPath = _listView.SelectedItem is int selectedItem
                && _renderedEntries is { } renderedEntries
                && selectedItem >= 0
                && selectedItem < renderedEntries.Count
                    ? renderedEntries[selectedItem].Identity
                    : null;

            var nameColumnWidth = EntryRowFormatter.ComputeNameColumnWidth(entries);
            var rows = new ObservableCollection<string>();
            for (var i = 0; i < entries.Count; i++)
            {
                rows.Add(EntryRowFormatter.Format(entries[i], nameColumnWidth));
            }

            _listView.SetSource(rows);

            // If we just navigated up, try to re-select the child we came from.
            var restore = _controller.RestoredSelection;
            _controller.ConsumeRestoredSelection();

            int selectedIndex = 0;
            if (restore is not null || selectedPath is not null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if ((restore is not null
                         && (string.Equals(entries[i].Name, restore, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(entries[i].Identity, restore, StringComparison.OrdinalIgnoreCase)))
                        || (restore is null && string.Equals(entries[i].Identity, selectedPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            if (entries.Count > 0)
            {
                _listView.SelectedItem = selectedIndex;
                _listView.EnsureSelectedItemVisible();
            }

            _renderedEntries = entries;
        }

        Title = FormatTitle(_controller.DisplayPath, TabTitleWidth);
        _filterLabel.Text = _controller.Query.Length > 0
            ? $" /{_controller.Query}"
            : " / ";
        UpdateStatusText(entries.Count);

        SetNeedsDraw();

        // The tab header is not painted from this window's Title directly: in tab mode the Border
        // adornment hosts a TitleView whose text and measured length are only refreshed inside its
        // layout pass. Reassigning Title only marks this window for redraw, so the header keeps the
        // old directory until its border layout is invalidated. That reflow must also account for
        // the OTHER tabs (their cached header widths) to avoid overlapping headers, so it is the
        // host's responsibility. Notify it only on an actual directory change; filter/status-only
        // refreshes are skipped.
        if (!string.Equals(_renderedDirectory, _controller.DisplayPath, StringComparison.Ordinal))
        {
            _renderedDirectory = _controller.DisplayPath;
            BeginGitProbe(_controller.CurrentLocation);
            DirectoryChanged?.Invoke();
        }
    }

    protected override void OnSubViewsLaidOut(LayoutEventArgs args)
    {
        base.OnSubViewsLaidOut(args);
        UpdateStatusText(_controller.FilteredEntries.Count);
    }

    /// <summary>
    /// Reapplies <see cref="FormatTitle"/> using the current <see cref="TabTitleWidth"/> without
    /// triggering a full data refresh. Called by the host after updating <see cref="TabTitleWidth"/>
    /// so all tabs reformat their headers in the same layout pass.
    /// </summary>
    internal void RefreshTitle()
    {
        Title = FormatTitle(_controller.DisplayPath, TabTitleWidth);
    }

    /// Builds the window/console title so the most relevant part of the path stays visible when
    /// the tab strip is too narrow to show it in full. The leaf folder is placed first (it is
    /// never truncated by the terminal's trailing ellipsis), followed by the full path. If the
    /// full path itself is long, its leading segments are trimmed to a head ellipsis so the
    /// deepest folders remain readable in natural order.
    /// </summary>
    private static string FormatTitle(string directory, int tabTitleWidth)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return "FileManager".PadRight(tabTitleWidth);
        }

        var leaf = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(leaf))
        {
            // A drive root such as "C:\" has no file name; show it as-is.
            var root = directory;
            return root.Length >= tabTitleWidth ? root[..tabTitleWidth] : root.PadRight(tabTitleWidth);
        }

        var path = ShortenPathHead(directory, MaxPathTitleLength);
        var title = $"{leaf} - {path}";
        title = title.Length >= tabTitleWidth ? title[..tabTitleWidth] : title.PadRight(tabTitleWidth);
        return title;
    }

    /// <summary>
    /// Trims the leading segments of <paramref name="path"/> to a head ellipsis when it exceeds
    /// <paramref name="maxLength"/>, keeping the right-most (deepest) portion of the path.
    /// </summary>
    private static string ShortenPathHead(string path, int maxLength)
    {
        if (path.Length <= maxLength)
        {
            return path;
        }

        const string ellipsis = "…";
        var keep = maxLength - ellipsis.Length;
        var tail = path[^keep..];

        // Prefer to start the visible tail at a path separator so segments are not cut mid-name.
        var separator = tail.IndexOf(Path.DirectorySeparatorChar);
        if (separator > 0 && separator < tail.Length - 1)
        {
            tail = tail[separator..];
        }

        return ellipsis + tail;
    }

    private static IReadOnlyList<FileSystemEntry> CollapseNestedSelections(IReadOnlyList<FileSystemEntry> entries)
    {
        if (entries.Count < 2)
        {
            return entries;
        }

        var selectedDirectories = new HashSet<string>(
            entries.Where(entry => entry.IsDirectory).Select(entry => entry.FullPath),
            StringComparer.OrdinalIgnoreCase);
        if (selectedDirectories.Count == 0)
        {
            return entries;
        }

        var collapsed = new List<FileSystemEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var parentPath = Path.GetDirectoryName(entry.FullPath);
            var nested = false;
            while (parentPath is not null)
            {
                if (selectedDirectories.Contains(parentPath))
                {
                    nested = true;
                    break;
                }

                parentPath = Path.GetDirectoryName(parentPath);
            }

            if (!nested)
            {
                collapsed.Add(entry);
            }
        }

        return collapsed;
    }

    private static FileSystemEntry CreateEntryFromDisk(string fullPath, bool isDirectory)
    {
        try
        {
            return new FileSystemEntry(
                Path.GetFileName(fullPath),
                fullPath,
                isDirectory,
                isDirectory ? 0 : new FileInfo(fullPath).Length,
                File.GetLastWriteTime(fullPath),
                File.GetAttributes(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Created {Path} but failed to read its display metadata", fullPath);
            return CreateFallbackEntry(fullPath, isDirectory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected failure reading display metadata for created entry {Path}", fullPath);
            return CreateFallbackEntry(fullPath, isDirectory);
        }
    }

    private static FileSystemEntry CreateFallbackEntry(string fullPath, bool isDirectory) => new(
        Path.GetFileName(fullPath),
        fullPath,
        isDirectory,
        0,
        DateTime.Now,
        isDirectory ? FileAttributes.Directory : FileAttributes.Normal);

    private void UpdateStatusText(int count) =>
        _statusLabel.Text = BuildStatus(count, _statusLabel.Viewport.Width);

    private string BuildStatus(int count, int availableWidth)
    {
        var builder = new StringBuilder();
        builder.Append(' ').Append(count).Append(count == 1 ? " item" : " items");

        if (!string.IsNullOrEmpty(_controller.StatusMessage))
        {
            builder.Append("  |  ").Append(_controller.StatusMessage);
        }

        builder.Append("  |  Ctrl+Alt+H help");
        var status = builder.ToString();
        var path = _controller.DisplayPath;
        var repository = _controller.GitRepository;
        var branch = repository?.Branch;
        var remote = repository?.RemoteRepository;

        // Before the first layout there is no usable viewport width. Show the full path now;
        // OnSubViewsLaidOut will constrain it as soon as the actual width is known.
        if (availableWidth <= 0)
        {
            return ComposeStatus(status, path, branch, remote);
        }

        const int separatorWidth = 5; // "  |  "
        var statusWidth = status.GetColumns(true);
        if (statusWidth >= availableWidth)
        {
            return status;
        }

        var pathWidth = path.GetColumns(true);
        var branchWidth = branch?.GetColumns(true) ?? 0;
        var remoteWidth = remote?.GetColumns(true) ?? 0;
        var componentCount = 1 + (branch is null ? 0 : 1) + (remote is null ? 0 : 1);
        var overflow = statusWidth
                       + componentCount * separatorWidth
                       + pathWidth
                       + branchWidth
                       + remoteWidth
                       - availableWidth;

        // Preserve the branch for longest: reduce or remove remote first, then path, then branch.
        ReduceWidth(ref remoteWidth, remote is null ? 0 : 1, ref overflow);
        if (overflow > 0 && remote is not null)
        {
            overflow -= remoteWidth + separatorWidth;
            remote = null;
        }

        ReduceWidth(ref pathWidth, 1, ref overflow);
        if (overflow > 0)
        {
            overflow -= pathWidth + separatorWidth;
            path = string.Empty;
        }

        ReduceWidth(ref branchWidth, branch is null ? 0 : 1, ref overflow);
        if (overflow > 0 || branchWidth == 0)
        {
            branch = null;
        }

        return ComposeStatus(
            status,
            path.Length == 0 ? null : TruncateTail(path, pathWidth),
            branch is null ? null : TruncateTail(branch, branchWidth),
            remote is null ? null : TruncateTail(remote, remoteWidth));
    }

    private static void ReduceWidth(ref int width, int minimum, ref int overflow)
    {
        if (overflow <= 0 || width <= minimum)
        {
            return;
        }

        var reduction = Math.Min(overflow, width - minimum);
        width -= reduction;
        overflow -= reduction;
    }

    private static string ComposeStatus(
        string status,
        string? path,
        string? branch,
        string? remote)
    {
        var builder = new StringBuilder(status);
        AppendStatusComponent(builder, path);
        AppendStatusComponent(builder, branch);
        AppendStatusComponent(builder, remote);
        return builder.ToString();
    }

    private static void AppendStatusComponent(StringBuilder builder, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            builder.Append("  |  ").Append(value);
        }
    }

    private static string TruncateTail(string value, int maxColumns)
    {
        if (value.GetColumns(true) <= maxColumns)
        {
            return value;
        }

        const string ellipsis = "…";
        var prefixColumns = 0;
        var prefixLength = 0;
        var maxPrefixColumns = maxColumns - ellipsis.GetColumns(true);

        foreach (var rune in value.EnumerateRunes())
        {
            var runeColumns = Math.Max(0, rune.GetColumns());
            if (prefixColumns + runeColumns > maxPrefixColumns)
            {
                break;
            }

            prefixColumns += runeColumns;
            prefixLength += rune.Utf16SequenceLength;
        }

        return value[..prefixLength] + ellipsis;
    }

    private enum ConflictChoice { None, Replace, Duplicate }

    private sealed class FlattenOperation
    {
        public object SyncRoot { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public List<FileSystemEntry> PendingEntries { get; } = [];
        public DirectoryTreeResult? Result { get; set; }
        public Exception? Error { get; set; }
        public bool Completed { get; set; }
        public bool Abandoned { get; set; }
    }
}

// P/Invoke wrapper for the shell's "Properties" dialog, used by Ctrl+Alt+P. Delegates entirely
// to Explorer's own shell UI, so no custom dialog needs to be implemented or maintained.
internal static class NativeMethods
{
    internal const uint SHOP_FILEPATH = 0x2;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObject, string? pszPage);
}
