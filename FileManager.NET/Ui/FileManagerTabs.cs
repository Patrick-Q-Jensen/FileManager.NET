using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using FileManager.NET.Core.Favorites;
using FileManager.NET.Core.FileSystem;
using FileManager.NET.Core.Filtering;
using FileManager.NET.Core.Navigation;
using FileManager.NET.Platform;

namespace FileManager.NET.Ui;

/// <summary>
/// Root window for the application. Hosts one or more <see cref="FileManagerWindow"/> instances
/// as tabs inside a Terminal.Gui <see cref="Tabs"/> container. Handles tab-management key chords
/// (Ctrl+T duplicate, Ctrl+Tab cycle, Ctrl+1-9 direct jump); these are left unhandled by the
/// focused pane and therefore bubble up to this window's <see cref="OnKeyDown"/>.
/// </summary>
internal sealed class FileManagerTabs : Window
{
    private const int MaxTabs = 9;

    private readonly IApplication _app;
    private readonly IDirectoryService _directoryService;
    private readonly IEntryFilter _entryFilter;
    private readonly IFileLauncher _fileLauncher;
    private readonly IFavoritesService _favoritesService;
    private readonly Tabs _tabs;

    // Guards against queuing more than one deferred tab-strip refresh at a time; rapid navigations
    // across one or more tabs coalesce into a single layout pass.
    private bool _headerRefreshQueued;

    internal FileManagerTabs(
        IApplication app,
        IDirectoryService directoryService,
        IEntryFilter entryFilter,
        IFileLauncher fileLauncher,
        IFavoritesService favoritesService,
        string startDirectory)
    {
        _app = app;
        _directoryService = directoryService;
        _entryFilter = entryFilter;
        _fileLauncher = fileLauncher;
        _favoritesService = favoritesService;

        // The host window is only a full-screen frame; the Tabs container fills it and each
        // FileManagerWindow added to it becomes a tab. No outer border avoids a redundant frame
        // around the tab strip.
        BorderStyle = LineStyle.None;

        _tabs = new Tabs
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        Add(_tabs);

        OpenTab(startDirectory);
    }

    /// <summary>
    /// Intercepts tab-management Ctrl chords that bubbled up unhandled from the focused pane.
    /// Ctrl+T duplicates the current tab (up to <see cref="MaxTabs"/>), Ctrl+Tab cycles to the
    /// next tab, and Ctrl+1-9 jumps directly to the corresponding tab.
    /// </summary>
    protected override bool OnKeyDown(Key key)
    {
        if (key.IsCtrl)
        {
            var baseKey = key.KeyCode & ~(KeyCode.CtrlMask | KeyCode.AltMask);

            switch (baseKey)
            {
                case KeyCode.T:
                    DuplicateCurrentTab();
                    key.Handled = true;
                    return true;

                case KeyCode.Tab:
                    CycleToNextTab();
                    key.Handled = true;
                    return true;
            }

            int tabIndex = GetDigitTabIndex(baseKey);
            if (tabIndex >= 0)
            {
                GoToTab(tabIndex);
                key.Handled = true;
                return true;
            }
        }

        return base.OnKeyDown(key);
    }

    /// <summary>
    /// Creates a new <see cref="FileManagerWindow"/> browsing <paramref name="directory"/>,
    /// adds it as a tab, and focuses it.
    /// </summary>
    private void OpenTab(string directory)
    {
        var controller = new NavigationController(_directoryService, _entryFilter, _fileLauncher);
        var pane = new FileManagerWindow(_app, controller, _favoritesService, directory);

        // When any tab changes directory its header title changes width, so refresh the whole tab
        // strip to keep all headers reflowed and non-overlapping.
        pane.DirectoryChanged += RefreshTabHeaders;

        _tabs.Add(pane);
        _tabs.Value = pane;
        pane.SetFocus();
    }

    private void RefreshTabHeaders()
    {
        if (_headerRefreshQueued)
        {
            return;
        }

        _headerRefreshQueued = true;

        _app.Invoke(() =>
        {
            _headerRefreshQueued = false;

            var ordered = _tabs.TabCollection.ToList();

            // Invalidate tab 1 first so its width is the fresh anchor for all subsequent offset calculations.
            if (ordered.Count > 0)
            {
                ordered[0].Border?.View?.SetNeedsLayout();
            }

            foreach (var tab in ordered.Skip(1))
            {
                tab.Border?.View?.SetNeedsLayout();
            }

            _tabs.SetNeedsLayout();
            _app.LayoutAndDraw(false);
        });
    }

    /// <summary>
    /// Opens a new tab starting in the same directory as the currently active tab.
    /// Does nothing when the tab limit of <see cref="MaxTabs"/> is already reached.
    /// </summary>
    private void DuplicateCurrentTab()
    {
        if (_tabs.TabCollection.Count() >= MaxTabs)
        {
            return;
        }

        var currentDirectory = (_tabs.Value as FileManagerWindow)?.CurrentDirectory
            ?? Environment.CurrentDirectory;

        OpenTab(currentDirectory);
    }

    /// <summary>Advances focus to the next tab, wrapping around from the last tab to the first.</summary>
    private void CycleToNextTab()
    {
        var tabs = _tabs.TabCollection.ToList();
        if (tabs.Count <= 1)
        {
            return;
        }

        int current = _tabs.IndexOf(_tabs.Value!);
        int next = (current + 1) % tabs.Count;
        _tabs.Value = tabs[next];
        tabs[next].SetFocus();
    }

    /// <summary>Switches directly to the tab at <paramref name="zeroBasedIndex"/>.</summary>
    private void GoToTab(int zeroBasedIndex)
    {
        var tabs = _tabs.TabCollection.ToList();
        if (zeroBasedIndex >= tabs.Count)
        {
            return;
        }

        _tabs.Value = tabs[zeroBasedIndex];
        tabs[zeroBasedIndex].SetFocus();
    }

    /// <summary>
    /// Returns the zero-based tab index for digit keys 1-9 (Ctrl+1 → 0, Ctrl+9 → 8),
    /// or -1 when <paramref name="key"/> is not a digit in that range.
    /// Terminal.Gui v2 maps digit keys to their Unicode/ASCII character values.
    /// </summary>
    private static int GetDigitTabIndex(KeyCode key)
    {
        int code = (int)key;
        if (code >= (int)'1' && code <= (int)'9')
        {
            return code - (int)'1';
        }

        return -1;
    }
}
