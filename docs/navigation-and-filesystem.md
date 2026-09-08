# Navigation and Filesystem

## Purpose

Describes directory and archive navigation, entry state, filtering, sorting, auto-refresh, and
recursive flattened views.

## Read this when

- Modifying `NavigationController`, `DirectoryService`, or `ZipArchiveService`
- Changing filtering, sorting, directory traversal, archives, or refresh behavior
- Adding a new location type or entry source
- Optimizing large directory views

## Related code

- `FileManager.NET\Core\Navigation`
- `FileManager.NET\Core\FileSystem\DirectoryService.cs`
- `FileManager.NET\Core\FileSystem\FileSystemEntry.cs`
- `FileManager.NET\Core\FileSystem\ZipArchiveService.cs`
- `FileManager.NET\Core\Filtering`
- `FileManager.NET\Core\Sorting`
- `FileManager.NET\Ui\FileManagerWindow.cs`

## Core model

`FileSystemEntry` is the common row model for disk and archive entries. It carries display name,
physical path, directory metadata, archive identity, and optional relative parent information used
by flattened views.

`NavigationLocation` represents either:

- A physical directory, or
- A ZIP file plus an internal archive directory.

`NavigationState` stores the current location, all entries, filtered entries, query, status, and Git
context. `NavigationController` owns state transitions and raises `Changed`; it contains no
Terminal.Gui types.

## Loading and navigation

`DirectoryService.Load` returns `DirectoryListing`, including an optional non-fatal error. Expected
I/O failures are represented rather than thrown.

`NavigationController`:

- Loads physical directories through `IDirectoryService`.
- Loads ZIP directories through `ZipArchiveService`.
- Clears the query when entering a new location.
- Preserves a selection identity when moving to a parent or reloading after a mutation.
- Opens physical files through `IFileLauncher`.
- Treats `.zip` files as navigable archive roots.

Session restoration uses `TryEnterLocation`; inaccessible saved locations are skipped without
preventing other tabs from opening.

## Filtering and sorting

Every state mutation that changes visible entries calls the shared filter/sort pipeline:

1. `IEntryFilter.Filter` applies case-insensitive substring filtering.
2. The result is sorted with `EntryComparer`.
3. `FilteredEntries` is replaced.
4. `Changed` notifies the pane.

Sort order is either:

- The persisted global default from `ISortSettingsService`, or
- A per-pane override.

Panes following the global mode re-sort when it changes. Locally overridden panes do not.

Filtering and sorting currently operate over the complete accumulated source list. Treat repeated
full-list filtering, sorting, and row formatting as performance-sensitive for large flattened trees.

## Automatic refresh

The pane checks for external changes every 10 seconds when not flattened:

- Physical locations are synchronously reloaded on the UI timer.
- Archive index refresh runs in the background, then performs a cache-backed comparison on the UI
  thread.
- Entry comparison is order-independent to avoid refreshes caused only by filesystem enumeration
  order.
- If content is unchanged, query, selection, and scroll position remain untouched.

Expected refresh failures are logged and leave the existing view usable.

## Flattened view

Ctrl+E recursively displays descendants of the current physical directory:

1. `BeginFlatten` initializes controller state.
2. A worker calls `DirectoryService.EnumerateTree`.
3. The service traverses with a depth-first stack and emits batches of 256 entries.
4. A 100 ms UI timer coalesces pending worker batches.
5. `AppendFlattenBatch` publishes entries through the normal filter/sort pipeline.
6. Completion reports found and skipped-path counts.

Traversal rules:

- Reparse-point directories are displayed but never traversed.
- Cancellation stops enumeration and discards unpublished work.
- Expected access and I/O errors increment the skipped count.
- Unexpected errors preserve entries already found and surface a failure status.
- ZIP archive locations cannot be flattened.
- `RelativeParentPath` identifies an entry's location beneath the flatten root.

Mutations in a completed flattened view update the accumulated entry set directly when safe:
deletion removes descendants, rename rewrites descendant paths, and root creation adds an entry.
Mutations during active enumeration stop and restart the scan.

## ZIP navigation

`ZipArchiveService` presents archive directories as `DirectoryListing` values and caches an index
keyed by archive file metadata. Mutations invalidate or rebuild the index.

Archive paths use `/` separators regardless of Windows path separators. Path normalization must
prevent traversal outside the archive destination during extraction.

ZIP operations include navigation, extraction, delete, rename, create, add entries, and archive
creation. Flattening archive content is intentionally unsupported.

## Performance invariants

- Avoid extra filesystem metadata passes during ordinary loading.
- Do not follow reparse points recursively.
- Publish recursive results in bounded batches.
- Do not perform blocking disk or archive work in high-frequency draw callbacks.
- Preserve cancellation and partial-result behavior when parallelizing work.
- Measure enumeration, filtering/sorting, formatting, and UI publication separately before
  optimizing; faster traversal alone may not improve end-to-end latency.

## Extension checklist

When adding a location or entry source:

1. Define its identity, parent navigation, activation, and selection-restoration behavior.
2. Return expected failures as result data.
3. Reuse the shared filter/sort pipeline.
4. Specify auto-refresh and cache invalidation.
5. Specify flatten support or an explicit unsupported status.
6. Verify path normalization and traversal boundaries.
