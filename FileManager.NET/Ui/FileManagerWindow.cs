using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using FileManager.NET.Core.Favorites;
using FileManager.NET.Core.FileSystem;
using FileManager.NET.Core.Navigation;

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

    private readonly IApplication _app;
    private readonly NavigationController _controller;
    private readonly IFavoritesService _favoritesService;
    private readonly Label _filterLabel;
    private readonly FilterListView _listView;
    private readonly Label _statusLabel;

    // Tracks the entry set currently rendered so status-only refreshes (e.g. after a command)
    // don't rebuild the list and reset the selection back to the top.
    private IReadOnlyList<FileSystemEntry>? _renderedEntries;

    // Tracks the directory the tab header was last drawn for. When it changes (drill-down or
    // move to parent) DirectoryChanged is raised so the host can refresh the tab strip.
    private string? _renderedDirectory;

    /// <summary>
    /// Raised when this tab has navigated to a different directory (and therefore its tab header
    /// title changed). The host uses this to refresh the whole tab strip so headers reflow without
    /// overlapping. Not raised for filter edits or status-only updates.
    /// </summary>
    internal event Action? DirectoryChanged;

    /// <summary>The directory currently displayed in this tab.</summary>
    internal string CurrentDirectory => _controller.CurrentDirectory;

    public FileManagerWindow(IApplication app, NavigationController controller, IFavoritesService favoritesService, string startDirectory)
    {
        _app = app;
        _controller = controller;
        _favoritesService = favoritesService;

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

        _controller.EnterDirectory(startDirectory);
        _listView.SetFocus();
    }

    private void OnCharacterTyped(char character) => _controller.AppendToQuery(character);

    private void OnEntryKeyDown(object? sender, Key key)
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
        }
    }

    /// <summary>
    /// Handles a Ctrl-initiated command from <paramref name="key"/>. Modifier state is read from the
    /// reliable <see cref="Key.IsShift"/>/<see cref="Key.IsCtrl"/> properties (the Shift bit in
    /// <see cref="Key.KeyCode"/> is not independently set for alpha keys), and Shift selects between
    /// command variants (e.g. Ctrl+C copies the name, Ctrl+Shift+C copies the full path). Returns
    /// <c>true</c> when the key mapped to a command. New commands are added as cases here.
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

            case KeyCode.P:
                CopySelectedPathToClipboard();
                return true;

            case KeyCode.C:
                CopySelectedNameToClipboard();
                return true;

            case KeyCode.X:
                ShowExecuteDialog();
                return true;

            case KeyCode.D:
                ShowDrivesDialog();
                return true;

            default:
                return false;
        }
    }

    private void CopySelectedNameToClipboard()
    {
        var entry = _controller.GetEntry(_listView.SelectedItem ?? -1);
        if (entry is null)
        {
            _controller.SetStatus("Nothing selected to copy.");
            return;
        }

        _controller.SetStatus(_app.Clipboard.TrySetClipboardData(entry.Name)
            ? $"Copied name: {entry.Name}"
            : "Clipboard is not available.");
    }

    private void CopySelectedPathToClipboard()
    {
        var entry = _controller.GetEntry(_listView.SelectedItem ?? -1);
        if (entry is null)
        {
            _controller.SetStatus("Nothing selected to copy.");
            return;
        }

        _controller.SetStatus(_app.Clipboard.TrySetClipboardData(entry.FullPath)
            ? $"Copied path: {entry.FullPath}"
            : "Clipboard is not available.");
    }

    private void AddCurrentDirectoryToFavorites()
    {
        var directory = _controller.CurrentDirectory;

        // Fire-and-forget: keep the UI responsive; status is updated when the task completes.
        _ = _favoritesService.AddAsync(directory).ContinueWith(
            t => _controller.SetStatus(t.Result
                ? $"Added to favorites: {directory}"
                : $"Already in favorites: {directory}"),
            TaskScheduler.FromCurrentSynchronizationContext());
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

        // Confirm via the ListView's Accept command (raised by Enter). This is the semantic
        // "user picked the selected item" signal; capturing it here lets Enter work reliably.
        // Cancel is left to the Dialog's built-in Esc handling, so we add no custom Esc key handler.
        listView.Accepting += (_, e) =>
        {
            chosen = drives[listView.SelectedItem ?? 0];
            e.Handled = true;
            _app.RequestStop();
        };

        dialog.Add(listView);
        listView.SetFocus();

        _app.Run(dialog);

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

        var listView = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
        };
        listView.SetSource(new ObservableCollection<string>(favorites));

        var dialog = new Dialog
        {
            Title = "Favorites",
            Width = Dim.Percent(70),
            Height = Dim.Percent(60),
        };

        // See ShowDrivesDialog: confirm via the ListView's Accept command; cancel via the Dialog's
        // built-in Esc handling. No custom Esc handler on the focused view keeps arrow-key
        // navigation off the escape-sequence timeout path.
        listView.Accepting += (_, e) =>
        {
            chosen = favorites[listView.SelectedItem ?? 0];
            e.Handled = true;
            _app.RequestStop();
        };

        dialog.Add(listView);
        listView.SetFocus();

        _app.Run(dialog);

        if (chosen is not null && Directory.Exists(chosen))
        {
            _controller.EnterDirectory(chosen);
        }
        else if (chosen is not null)
        {
            _controller.SetStatus($"Directory no longer exists: {chosen}");
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

        _app.Run(dialog);

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
        }
    }

    private void Refresh()
    {
        var entries = _controller.FilteredEntries;

        // Only rebuild the list source and reset the selection when the entry set actually
        // changed (directory change or filter edit). Status-only updates, such as after a
        // command like Ctrl+C, keep the current selection intact.
        if (!ReferenceEquals(entries, _renderedEntries))
        {
            var rows = new ObservableCollection<string>();
            for (var i = 0; i < entries.Count; i++)
            {
                rows.Add(EntryRowFormatter.Format(entries[i]));
            }

            _listView.SetSource(rows);
            if (entries.Count > 0)
            {
                _listView.SelectedItem = 0;
                _listView.EnsureSelectedItemVisible();
            }

            _renderedEntries = entries;
        }

        Title = FormatTitle(_controller.CurrentDirectory);
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
    /// Builds the window/console title so the most relevant part of the path stays visible when
    /// the tab strip is too narrow to show it in full. The leaf folder is placed first (it is
    /// never truncated by the terminal's trailing ellipsis), followed by the full path. If the
    /// full path itself is long, its leading segments are trimmed to a head ellipsis so the
    /// deepest folders remain readable in natural order.
    /// </summary>
    private static string FormatTitle(string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return "FileManager";
        }

        var leaf = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(leaf))
        {
            // A drive root such as "C:\" has no file name; show it as-is.
            return directory;
        }

        var path = ShortenPathHead(directory, MaxPathTitleLength);
        return $"{leaf} — {path}";
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

    private string BuildStatus(int count)
    {
        var builder = new StringBuilder();
        builder.Append(' ').Append(count).Append(count == 1 ? " item" : " items");

        if (!string.IsNullOrEmpty(_controller.StatusMessage))
        {
            builder.Append("  |  ").Append(_controller.StatusMessage);
        }

        builder.Append("  |  \u2190 up   \u2192 open dir   Enter open   Bksp edit filter   Esc clear/quit   Ctrl+C copy name   Ctrl+P copy path   Ctrl+F favorites   Ctrl+Alt+F add favorite   Ctrl+D drives   Ctrl+X execute   Ctrl+Q quit   Ctrl+T new tab   Ctrl+Tab next tab   Ctrl+1-9 go to tab");
        return builder.ToString();
    }
}
