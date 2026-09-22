# UI and Input

## Purpose

Explains Terminal.Gui view ownership, focus, keyboard routing, dialogs, refreshes, and safe background
result publication.

## Read this when

- Modifying `FileManagerTabs`, `FileManagerWindow`, or `FilterListView`
- Adding or changing keyboard shortcuts
- Changing focus, tabs, dialogs, selection, or marking
- Publishing worker results to the UI

## Related code

- `FileManager.NET\Ui\FileManagerTabs.cs`
- `FileManager.NET\Ui\FileManagerWindow.cs`
- `FileManager.NET\Ui\FilterListView.cs`
- `FileManager.NET\Ui\EntryRowFormatter.cs`
- `FileManager.NET\Core\Navigation\NavigationController.cs`

## View hierarchy

```text
FileManagerTabs (root Window)
  -> Tabs
       -> FileManagerWindow (one per tab)
            -> filter Label
            -> FilterListView
            -> status Label
```

`FileManagerTabs` owns tab creation, restoration, switching, closure, title sizing, and shared
services. A `FileManagerWindow` owns one pane's controls, commands, dialogs, timers, and controller
subscriptions.

The list is the normal focus target. New, restored, and switched tabs explicitly focus their pane or
list.

## Keyboard routing

Input handling is intentionally layered:

1. `FilterListView.OnKeyDown` claims unmodified printable characters and raises `CharacterTyped` for
   substring filtering.
2. Native `ListView` handling owns ordinary movement and marking.
3. `FileManagerWindow` handles pane navigation and Ctrl/Alt commands.
4. Unhandled tab-management commands can bubble to `FileManagerTabs`.

`FilterListView.OnKeyDownNotHandled` consumes navigation keys at list boundaries. Without this guard,
an arrow key rejected by `ListView` can bubble into the `Tabs` control and switch tabs.

Terminal.Gui controls have built-in commands. Do not assume an unhandled key is harmless:

- Plain Escape is application-owned. The pane clears the filter and focuses the list.
- `FileManagerTabs` also consumes Escape because clicking a tab header can leave focus on `Tabs`;
  allowing it to reach the root `Window` invokes Terminal.Gui's top-level quit behavior.
- Ctrl+M is indistinguishable from Enter in terminal input and must not be assigned separately.
- Some Ctrl+Alt combinations can be reported as AltGr-composed characters, so modifier checks use
  `Key.IsCtrl` and `Key.IsAlt`.

## Command ownership

| Owner | Representative commands |
|---|---|
| `FilterListView` | Printable filtering, native movement, Space marking |
| `FileManagerWindow` | Enter, arrows, Delete, Backspace, Escape, file operations, dialogs, sort, flatten, favorites |
| `FileManagerTabs` | Duplicate/close/switch tabs and root-level Escape fallback |

F1-F9 switch directly to tabs 1-9. The application caps tabs and favorites at nine.

When adding a command:

1. Decide which focused control should own it.
2. Check Terminal.Gui's built-in binding for the same key.
3. Set `key.Handled` and return a handled result when application behavior is complete.
4. Test with focus on the list, the tab header, and any relevant dialog control.
5. Keep the help dialog synchronized with user-visible bindings.

## Controller refresh contract

`NavigationController.Changed` causes `FileManagerWindow.Refresh` on the UI thread. Refresh:

- Rebuilds formatted rows from `FilteredEntries`.
- Restores selection by entry identity when requested.
- Updates the query, status, title, Git context, and tab-header layout.

`EntryRowFormatter` sizes the name column to the widest visible entry, with a 20-character minimum
and a 160-character ceiling. Longer names are ellipsized to keep row allocation and horizontal
overflow bounded; date and size columns remain fixed-width.

Prefer a controller mutation followed by its existing `Changed` event. Do not mutate list content in
parallel with controller state.

Selection restoration uses entry identity rather than display index because filtering, sorting, and
flatten mutations can reorder entries. Marked entries must be resolved before a mutation, and nested
selections should be collapsed where operating on a parent already covers descendants.

## Dialogs and long operations

Dialogs run as nested Terminal.Gui top-levels. Use the established helpers:

- `RunDialog` preserves and restores the console title.
- `RunBackgroundOperation` displays progress while work runs off the UI thread.
- Paste and ZIP operations publish progress through `IProgress<T>` and `IApplication.Invoke`.
- Dialog Escape behavior belongs to the dialog unless the dialog explicitly implements cancellation.
- Call `RequestStop(dialog)` when a specific asynchronous dialog must close; avoid accidentally
  stopping the application root.

Continuations must:

- Marshal UI work through `_app.Invoke`.
- Verify the pane is not disposed.
- Verify captured location/operation identity before publishing.
- Handle cancellation separately from failure.

## Timers and background producers

- The normal directory auto-refresh timer runs every 10 seconds.
- Archive refresh uses a background index load and applies only if the location is unchanged.
- Flatten progress is coalesced on a 100 ms UI timer.
- Git detection is cancellable and location-scoped.

Disposal sets the pane's disposed state, cancels active producers, removes timers, and unsubscribes
events before calling the base implementation.

## UI invariants

- The file list is the default focus target.
- Application-owned keys do not fall through to conflicting Terminal.Gui defaults.
- Background tasks never touch views directly.
- Stale background results never replace state for a newer location.
- Selection is restored by stable identity, not by stale row index.
- Dialog closure never unintentionally closes the root application.
- Large operations show progress and keep the UI responsive.
