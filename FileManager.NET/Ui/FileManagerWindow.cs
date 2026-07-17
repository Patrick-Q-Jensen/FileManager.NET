using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using FileManager.NET.Core.Favorites;
using FileManager.NET.Core.FileSystem;
using FileManager.NET.Core.Navigation;
using FileManager.NET.Core.Sorting;
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

    // How often the current directory is silently re-checked for external changes (files added,
    // removed, or renamed by another process). Kept fairly relaxed since this is a convenience
    // feature, not a live filesystem watch.
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(10);

    // Set by the host (FileManagerTabs) to keep all tab headers the same width. Defaults to 20
    // until the host computes the available-width-divided-by-tab-count value.
    internal int TabTitleWidth { get; set; } = 20;

    private readonly IApplication _app;
    private readonly NavigationController _controller;
    private readonly IFavoritesService _favoritesService;
    private readonly ISortSettingsService _sortSettingsService;
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

    /// <summary>The directory currently displayed in this tab.</summary>
    internal string CurrentDirectory => _controller.CurrentDirectory;

    public FileManagerWindow(IApplication app, NavigationController controller, IFavoritesService favoritesService, ISortSettingsService sortSettingsService, string startDirectory)
    {
        _app = app;
        _controller = controller;
        _favoritesService = favoritesService;
        _sortSettingsService = sortSettingsService;

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

        _controller.EnterDirectory(startDirectory);
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

        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _autoRefreshToken is not null)
        {
            _app.RemoveTimeout(_autoRefreshToken);
            _autoRefreshToken = null;
        }

        base.Dispose(disposing);
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
                _controller.Activate(_listView.SelectedItem ?? -1);
                key.Handled = true;
                return;

            case KeyCode.CursorRight:
                _controller.DrillInto(_listView.SelectedItem ?? -1);
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
                // Esc breaks out of filtering mode: clear an active filter first, and only quit
                // when there is nothing to clear. This keeps deleting the filter from ever moving
                // up a directory.
                if (_controller.Query.Length > 0)
                {
                    _controller.ClearQuery();
                }
                else
                {
                    _app.RequestStop();
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

            case KeyCode.C:
                CopySelectedItemToClipboard();
                return true;

            case KeyCode.V:
                PasteFromClipboard();
                return true;

            case KeyCode.X:
                ShowExecuteDialog();
                return true;

            case KeyCode.D:
                ShowDrivesDialog();
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
                _controller.DrillInto(_listView.SelectedItem ?? -1);
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

        var text = string.Join('\n', entries.Select(e => e.FullPath));
        _controller.SetStatus(_app.Clipboard.TrySetClipboardData(text)
            ? entries.Count == 1 ? $"Copied path: {entries[0].FullPath}" : $"Copied {entries.Count} paths"
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
        if (entries.Count == 0)
        {
            _controller.SetStatus("Nothing selected to copy.");
            return;
        }

        _controller.SetStatus(WindowsFileClipboard.TrySetFiles(entries.Select(e => e.FullPath).ToArray())
            ? entries.Count == 1 ? $"Copied: {entries[0].Name}" : $"Copied {entries.Count} items"
            : "Clipboard is not available.");
    }

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

    private void PasteFromClipboard()
    {
        if (!WindowsFileClipboard.TryGetFiles(out var sources))
        {
            _controller.SetStatus("No files on clipboard to paste.");
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

        int ok = 0;
        string? firstError = null;

        foreach (var source in sources)
        {
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
                name = source;

            bool isConflict = File.Exists(Path.Combine(_controller.CurrentDirectory, name))
                           || Directory.Exists(Path.Combine(_controller.CurrentDirectory, name));

            bool sourceIsDirectory = Directory.Exists(source);

            var dest = isConflict && conflictChoice == ConflictChoice.Duplicate
                ? GetUniqueDestPath(_controller.CurrentDirectory, name, sourceIsDirectory)
                : Path.Combine(_controller.CurrentDirectory, name);

            try
            {
                if (sourceIsDirectory)
                {
                    if (isConflict && conflictChoice == ConflictChoice.Replace)
                        MergeDirectory(source, dest);
                    else
                        CopyDirectory(source, dest);
                }
                else
                {
                    File.Copy(source, dest, overwrite: isConflict && conflictChoice == ConflictChoice.Replace);
                }

                ok++;
            }
            catch (Exception ex)
            {
                firstError ??= $"{name}: {ex.Message}";
                Log.Warning(ex, "Failed to paste {Source} to {Dest}", source, dest);
            }
        }

        _controller.EnterDirectory(_controller.CurrentDirectory);

        int total = sources.Count;
        _controller.SetStatus(firstError is null
            ? $"Pasted {ok} {(ok == 1 ? "item" : "items")}."
            : ok > 0
                ? $"Pasted {ok}/{total}: {firstError}"
                : $"Paste failed: {firstError}");
    }

    // Terminal.Gui sets Console.Title to the dialog Title on every Run() call.
    // Wrap all modal dialog runs through here to keep the title static.
    private void RunDialog(Dialog dialog)
    {
        var savedTitle = Console.Title;
        _app.Run(dialog);
        Console.Title = savedTitle;
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
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => d.Name)
            .ToList();

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
        listView.SetSource(new ObservableCollection<string>(drives));

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
        var entry = _controller.GetEntry(_listView.SelectedItem ?? -1);
        if (entry is null || entry.Name == "..")
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
            Text = $"Delete \"{entry.Name}\"? Press Enter to confirm.",
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

            _controller.SetStatus($"Deleted: {entry.Name}");
            _controller.EnterDirectory(_controller.CurrentDirectory);
        }
        catch (Exception ex)
        {
            _controller.SetStatus($"Delete failed: {ex.Message}");
            Log.Warning(ex, "Failed to delete {Path}", entry.FullPath);
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

        var newFullPath = Path.Combine(_controller.CurrentDirectory, newName);

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

            _controller.ReloadSelectingEntry(newName);
            _controller.SetStatus($"Renamed: {entry.Name} \u2192 {newName}");
        }
        catch (Exception ex)
        {
            _controller.SetStatus($"Rename failed: {ex.Message}");
            Log.Warning(ex, "Failed to rename {Path} to {NewName}", entry.FullPath, newName);
        }
    }

    private void ShowExecuteDialog()
    {
        var entry = _controller.GetEntry(_listView.SelectedItem ?? -1);
        if (entry is null)
        {
            _controller.SetStatus("Nothing selected to execute.");
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

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = entry.FullPath,
                Arguments = args,
                UseShellExecute = true,
            });

            _controller.SetStatus($"Launched: {entry.Name}");
        }
        catch (Exception ex)
        {
            _controller.SetStatus($"Launch failed: {ex.Message}");
            Log.Warning(ex, "Failed to launch {Path} with args {Args}", entry.FullPath, args);
        }
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
                Process.Start(new ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true,
                });
                _controller.SetStatus($"Opened: {Path.GetFileName(fullPath)}");
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
            "  \u2192               Drill into directory",
            "  \u2190               Go to parent directory",
            "  Enter           Open file or directory",
            "  Del             Delete selected item",
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
            "  Ctrl+B          Toggle marking mode (hint ctrl+A will select all, ctrl+u will unselect all)",
            "  Ctrl+D          Show drive picker",
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
            "  Ctrl+Alt+I      Move selection up  (vim-style)",
            "  Ctrl+Alt+K      Move selection down  (vim-style)",
            "  Ctrl+Alt+J      Go to parent  (vim-style)",
            "  Ctrl+Alt+L      Drill into directory  (vim-style)",
            "  Ctrl+Alt+P      Show Windows Properties dialog",
            "  Ctrl+Alt+O      Set global sort order",
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
        var entries = _controller.FilteredEntries;

        // Only rebuild the list source and reset the selection when the entry set actually
        // changed (directory change or filter edit). Status-only updates, such as after a
        // command like Ctrl+C, keep the current selection intact.
        if (!ReferenceEquals(entries, _renderedEntries))
        {
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
            if (restore is not null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (string.Equals(entries[i].Name, restore, StringComparison.OrdinalIgnoreCase))
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

        Title = FormatTitle(_controller.CurrentDirectory, TabTitleWidth);
        _filterLabel.Text = _controller.Query.Length > 0
            ? $" /{_controller.Query}"
            : " / ";
        _statusLabel.Text = BuildStatus(entries.Count);

        SetNeedsDraw();

        // The tab header is not painted from this window's Title directly: in tab mode the Border
        // adornment hosts a TitleView whose text and measured length are only refreshed inside its
        // layout pass. Reassigning Title only marks this window for redraw, so the header keeps the
        // old directory until its border layout is invalidated. That reflow must also account for
        // the OTHER tabs (their cached header widths) to avoid overlapping headers, so it is the
        // host's responsibility. Notify it only on an actual directory change; filter/status-only
        // refreshes are skipped.
        if (!string.Equals(_renderedDirectory, _controller.CurrentDirectory, StringComparison.Ordinal))
        {
            _renderedDirectory = _controller.CurrentDirectory;
            DirectoryChanged?.Invoke();
        }
    }

    /// <summary>
    /// <summary>
    /// Reapplies <see cref="FormatTitle"/> using the current <see cref="TabTitleWidth"/> without
    /// triggering a full data refresh. Called by the host after updating <see cref="TabTitleWidth"/>
    /// so all tabs reformat their headers in the same layout pass.
    /// </summary>
    internal void RefreshTitle()
    {
        Title = FormatTitle(_controller.CurrentDirectory, TabTitleWidth);
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

    private static string GetUniqueDestPath(string destDir, string name, bool isDirectory)
    {
        var candidate = Path.Combine(destDir, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;

        if (isDirectory)
        {
            for (int n = 2; ; n++)
            {
                candidate = Path.Combine(destDir, $"{name} ({n})");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    return candidate;
            }
        }

        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (int n = 2; ; n++)
        {
            candidate = Path.Combine(destDir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    // Copies the contents of source into an existing dest directory, overwriting conflicting
    // files. Subdirectories that already exist in dest are merged recursively; new ones are
    // created. This matches Windows Explorer's folder-conflict behaviour (merge, not replace).
    private static void MergeDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest); // No-op if dest already exists.
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            MergeDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private string BuildStatus(int count)
    {
        var builder = new StringBuilder();
        builder.Append(' ').Append(count).Append(count == 1 ? " item" : " items");

        if (!string.IsNullOrEmpty(_controller.StatusMessage))
        {
            builder.Append("  |  ").Append(_controller.StatusMessage);
        }

        builder.Append("  |  Ctrl+Alt+H help");
        return builder.ToString();
    }

    private enum ConflictChoice { None, Replace, Duplicate }
}

// P/Invoke wrapper for the shell's "Properties" dialog, used by Ctrl+Alt+P. Delegates entirely
// to Explorer's own shell UI, so no custom dialog needs to be implemented or maintained.
internal static class NativeMethods
{
    internal const uint SHOP_FILEPATH = 0x2;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObject, string? pszPage);
}

