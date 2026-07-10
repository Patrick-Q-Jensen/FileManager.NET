using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace FileManager.NET.Ui;

/// <summary>
/// A <see cref="ListView"/> that routes plain printable keystrokes to <see cref="CharacterTyped"/>
/// instead of the built-in prefix type-ahead (<see cref="ListView.KeystrokeNavigator"/>). This lets
/// the host implement "type to filter" with substring matching while keeping the native, virtualized
/// arrow/page navigation for every other key.
/// </summary>
internal sealed class FilterListView : ListView
{
    /// <summary>Raised when the user types a printable character to extend the live filter.</summary>
    public event Action<char>? CharacterTyped;

    protected override bool OnKeyDown(Key key)
    {
        // Claim printable characters before ListView's type-ahead navigator can consume them,
        // so the host filters by "contains" rather than the built-in "starts with" search.
        if (TryGetPrintable(key, out var character))
        {
            CharacterTyped?.Invoke(character);
            key.Handled = true;
            return true;
        }

        return base.OnKeyDown(key);
    }

    /// <summary>
    /// Runs only after the ListView's own key bindings have had their chance (this is raised by the
    /// framework when a key was NOT consumed by any command on this view). When the selection is
    /// already at the first/last item, the ListView leaves the arrow/page key unhandled, and it
    /// would then bubble up to the hosting Tabs container whose arrow bindings move the tab
    /// selection. Marking the list-navigation keys handled here stops that bubble without
    /// interfering with normal in-list movement (which is handled earlier, before this runs).
    /// </summary>
    protected override bool OnKeyDownNotHandled(Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.CursorUp:
            case KeyCode.CursorDown:
            case KeyCode.PageUp:
            case KeyCode.PageDown:
            case KeyCode.Home:
            case KeyCode.End:
                key.Handled = true;
                return true;
        }

        return base.OnKeyDownNotHandled(key);
    }

    private bool TryGetPrintable(Key key, out char character)
    {
        character = '\0';

        // Use the reliable modifier properties rather than the KeyCode bitmask: for some
        // Ctrl/Alt chords (e.g. Ctrl+Alt+M) the driver reports the composed character with the
        // modifier bits not reflected in KeyCode, which would otherwise leak into the filter.
        if (key.IsCtrl || key.IsAlt)
        {
            return false;
        }

        var value = key.AsRune.Value;
        if (value < 0x20 || value == 0x7f)
        {
            return false;
        }

        // While marking mode is on, Space is reserved for the native mark/unmark toggle instead
        // of being appended to the live filter query.
        if (ShowMarks && value == ' ')
        {
            return false;
        }

        character = (char)value;
        return true;
    }
}
