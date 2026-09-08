# Architecture and Application Lifetime

## Purpose

Describes component boundaries, dependency direction, object ownership, threading, and the
application lifecycle.

## Read this when

- Changing service composition or disposal
- Moving behavior between `Core`, `Ui`, and `Platform`
- Adding background work, timers, or shared services
- Introducing a cross-cutting feature

## Related code

- `FileManager.NET\Program.cs`
- `FileManager.NET\Core`
- `FileManager.NET\Ui\FileManagerTabs.cs`
- `FileManager.NET\Ui\FileManagerWindow.cs`
- `FileManager.NET\Platform`

## Layers

| Layer | Owns | Must not own |
|---|---|---|
| `Core` | Domain records, filesystem contracts, navigation state, filtering, sorting, persistence, Git detection, undo semantics | Terminal.Gui views or input handling |
| `Ui` | Terminal.Gui controls, focus, key routing, dialogs, progress presentation, and coordination of user operations | Native COM declarations or reusable filesystem algorithms |
| `Platform` | Windows-native clipboard, Shell operations, process launching, and command execution | Navigation state or Terminal.Gui presentation |
| `Program` | Concrete service construction, top-level lifetime, logging, session load/save | Feature logic |

The current dependency direction is:

```text
Program -> Ui -> Core
   |        |      |
   +--------+----> Platform
```

`NavigationController` currently consumes the narrow `Platform.IFileLauncher` abstraction, while
native implementations stay in `Platform`. `Core` must not reference Terminal.Gui. Keep new
operating-system behavior behind a focused managed contract when practical.

## Composition and ownership

`Program.Main` is the composition root:

1. Configure Serilog and unhandled-exception logging.
2. Load session state unless an explicit start directory was supplied.
3. Construct shared services.
4. Initialize `IApplication`.
5. Construct one `FileManagerTabs` root window.
6. Run the Terminal.Gui application.
7. Save open locations and the active tab during shutdown.
8. Dispose the root window, application, Recycle Bin service, and logger.

Shared across all tabs:

- `IDirectoryService`
- `IEntryFilter`
- `IGitRepositoryService`
- `IFileLauncher`
- `IRecycleService`
- `IFavoritesService`
- `ISortSettingsService`
- `ZipArchiveService`
- `UndoHistory`

Each tab gets its own `NavigationController` and `FileManagerWindow`. Consequently, location, query,
selection, flattened-view state, and local sort mode are tab-specific. Undo history and global sort
settings are application-wide.

The owner that creates a disposable resource must release it:

- `Program` owns the application and `IRecycleService`.
- `FileManagerTabs` owns panes, `ZipArchiveService`, and `UndoHistory`.
- `FileManagerWindow` removes timers, cancels probes/flattening, and unsubscribes events.
- A closed tab must be removed and disposed.

## State and rendering flow

Normal state changes follow one direction:

```text
Input or timer
  -> FileManagerWindow command
  -> NavigationController mutation
  -> NavigationController.Changed
  -> FileManagerWindow.Refresh
  -> Terminal.Gui controls
```

`NavigationController` is the source of truth for the current location, complete entry list, filtered
entry list, query, sort mode, status, Git context, and flatten state. Views should not independently
reimplement those transitions.

## Threading rules

- Terminal.Gui views and `NavigationController` mutations that trigger rendering belong on the UI
  thread.
- Use `IApplication.Invoke` to publish results from workers.
- Directory flattening and long paste/ZIP operations run away from the UI thread and report bounded
  progress.
- Git probes and archive refreshes capture the requested location and discard stale results.
- Windows Shell operations requiring COM apartment state use their dedicated STA worker.
- Cancellation sources, timers, event subscriptions, and native handles must be released during
  disposal.

Never update a view directly from a task continuation. Avoid synchronous waits on work that requires
the UI thread to complete.

## Error-handling rules

- Expected filesystem, persistence, and native failures are logged and surfaced as status or result
  data instead of terminating the process.
- Unexpected failures should be logged as errors and allowed to reach an existing operation boundary;
  do not silently convert them into success.
- Partial file operations must retain accurate changed-item and error collections.
- Cleanup is best-effort but must log failures that could leak resources or invalidate undo state.

## Architectural invariants

- `Core` contains no Terminal.Gui types.
- There is one navigation controller per tab.
- Cross-tab state is shared deliberately, never through static view state.
- All view mutation occurs on the Terminal.Gui thread.
- Long filesystem/native operations remain cancellable or visibly bounded.
- Expected environmental failures do not crash the application.
- Disposal cancels producers before releasing consumers or native resources.

## Adding a feature

1. Put reusable state and algorithms in `Core`.
2. Add the narrowest necessary platform contract for native behavior.
3. Compose the implementation in `Program`.
4. Orchestrate it from `FileManagerWindow`; do not move dialogs into `Core`.
5. Define partial-success, cancellation, refresh, and undo behavior before implementation.
6. Update the relevant topic document if the workflow or invariants change.
