using System.Collections.ObjectModel;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
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
    private readonly IApplication _app;
    private readonly NavigationController _controller;
    private readonly Label _filterLabel;
    private readonly FilterListView _listView;
    private readonly Label _statusLabel;

    public FileManagerWindow(IApplication app, NavigationController controller, string startDirectory)
    {
        _app = app;
        _controller = controller;

        BorderStyle = LineStyle.Rounded;

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
            case KeyCode.CursorRight:
                _controller.Activate(_listView.SelectedItem ?? -1);
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

        // Ctrl+Q quits. Printable characters are handled by FilterListView for live filtering.
        if ((key.KeyCode & KeyCode.CtrlMask) != 0
            && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.Q)
        {
            _app.RequestStop();
            key.Handled = true;
        }
    }

    private void Refresh()
    {
        var entries = _controller.FilteredEntries;

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

        Title = _controller.CurrentDirectory;
        _filterLabel.Text = _controller.Query.Length > 0
            ? $" /{_controller.Query}"
            : " / (type to filter)";
        _statusLabel.Text = BuildStatus(entries.Count);

        SetNeedsDraw();
    }

    private string BuildStatus(int count)
    {
        var builder = new StringBuilder();
        builder.Append(' ').Append(count).Append(count == 1 ? " item" : " items");

        if (!string.IsNullOrEmpty(_controller.StatusMessage))
        {
            builder.Append("  |  ").Append(_controller.StatusMessage);
        }

        builder.Append("  |  Enter open   \u2190 up   Bksp edit filter   Esc clear/quit   Ctrl+Q quit");
        return builder.ToString();
    }
}
