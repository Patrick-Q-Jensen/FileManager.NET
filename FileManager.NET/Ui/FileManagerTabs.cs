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
    /// Fallback handler for tab-management Ctrl chords in case focus lands on the Tabs container
    /// itself rather than on a pane. Panes handle these directly via injected delegates.
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

        // Wire tab-management commands so the pane handles them directly without bubbling.
        pane.SwitchToTab = GoToTab;
        pane.DuplicateTab = DuplicateCurrentTab;
        pane.CycleTab = CycleToNextTab;

        _tabs.Add(pane);
        _tabs.Value = pane;
        pane.SetFocus();

        // Double-defer: the first Invoke lets Tabs complete its initial layout pass (which sets
        // Frame.Width on the new tab); the second then runs ApplyTabTitleWidths with valid
        // dimensions. A single defer is not enough because Frame.Width is still 0 on the first
        // pump cycle after Add().
        _app.Invoke(() => _app.Invoke(ApplyTabTitleWidths));
    }

    /// <summary>
    /// Computes the per-tab header width in cells from the available container width divided by
    /// the current tab count, sets <see cref="BorderView.TabLength"/> on each tab directly so the
    /// framework uses an explicit fixed width instead of auto-sizing from the title string, and
    /// forces a layout/draw pass so headers reflow immediately.
    /// </summary>
    private void ApplyTabTitleWidths()
    {
        var tabs = _tabs.TabCollection.ToList();
        var tabCount = Math.Max(1, tabs.Count);

        // Use the root window frame width as the reliable available-width source; _tabs fills it
        // entirely via Dim.Fill() so this.Frame.Width equals the usable tab bar width.
        var tabWidth = Math.Max(8, this.Frame.Width / tabCount - 1);

        foreach (var tab in tabs)
        {
            // Set TabLength directly so Tabs uses an explicit pixel width rather than
            // auto-sizing from the title string (which would add border overhead on top).
            if (tab.Border?.View is BorderView borderView)
            {
                borderView.TabLength = tabWidth;
                borderView.SetNeedsLayout();
            }

            // Also reformat the title string to fit within the fixed width (subtract 2 for the
            // border cells Terminal.Gui adds around the text inside the tab header).
            if (tab is FileManagerWindow pane)
            {
                pane.TabTitleWidth = Math.Max(1, tabWidth - 2);
                pane.RefreshTitle();
            }
        }

        // Reset scroll so tab 1 is never pushed off-screen after a width recalculation.
        _tabs.ScrollOffset = 0;
        _tabs.SetNeedsLayout();
        _app.LayoutAndDraw(false);
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
            ApplyTabTitleWidths();
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
}
