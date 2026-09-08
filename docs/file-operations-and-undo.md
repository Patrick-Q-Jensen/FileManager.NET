# File Operations and Undo

## Purpose

Describes mutation workflows, partial-success reporting, Recycle Bin behavior, ZIP operations, and
session undo.

## Read this when

- Changing copy, paste, create, rename, move, delete, restore, or ZIP mutation
- Modifying `UndoHistory`, `IDirectoryService`, or `IRecycleService`
- Changing conflict, cancellation, or post-mutation refresh behavior

## Related code

- `FileManager.NET\Ui\FileManagerWindow.cs`
- `FileManager.NET\Core\FileSystem\DirectoryService.cs`
- `FileManager.NET\Core\FileSystem\IDirectoryService.cs`
- `FileManager.NET\Core\FileSystem\IRecycleService.cs`
- `FileManager.NET\Core\FileSystem\ZipArchiveService.cs`
- `FileManager.NET\Core\Undo\UndoHistory.cs`
- `FileManager.NET\Platform\WindowsFileClipboard.cs`
- `FileManager.NET\Platform\WindowsFileRecycleService.cs`

## Common mutation flow

File operations are orchestrated by `FileManagerWindow`:

1. Resolve marked entries, or the current entry when nothing is marked.
2. Exclude synthetic parent entries and collapse redundant nested selections.
3. Validate unsupported or dangerous inputs before starting.
4. Confirm destructive behavior where required.
5. Run expensive work off the UI thread with progress or a busy dialog.
6. Preserve per-item success and failure data.
7. Record undo only for changes that can be reversed safely.
8. Refresh the current normal or flattened view.
9. Surface a concise status without hiding partial failure.

Expected failures must not crash the UI or be reported as complete success.

## Copy and paste

Physical clipboard paths use Windows `CF_HDROP`. Virtual clipboard items, such as attachments copied
from another application, are described through `FileGroupDescriptorW` and materialized from
`FileContents` streams into a temporary staging directory.

`DirectoryService.PasteAsync`:

- Builds a copy plan before execution.
- Supports replace and duplicate conflict policies.
- Reports preparation, copying, and finalization progress.
- Uses asynchronous file copy with a 1 MiB buffer.
- Preserves completed files when cancellation is requested.
- Records created file/directory metadata for undo.
- Tracks whether replacement or incomplete tracking makes undo unsafe.

ZIP paste and archive creation use `ZipArchiveService` and report compatible `PasteProgress` data.
Archive rewrites must remain atomic enough to avoid replacing a valid archive with an incomplete
result.

Temporary virtual-file staging directories must be deleted after use, including cancellation and
failure paths.

## Create and rename

- Windows entry names are validated against invalid characters, trailing dot/space rules, and
  reserved device names.
- Rename restores selection to the new identity.
- Directory rename in flattened mode rewrites descendant physical paths.
- ZIP mutations operate on archive entry paths, not physical child paths.

## Delete and Recycle Bin

Physical deletion defaults to the Windows Recycle Bin:

1. `IRecycleService.RecycleAsync` receives the selected entries.
2. Successfully recycled items return `RecycledItemToken` values retained for session undo.
3. The service returns per-path failures and paths deleted without a recoverable Shell item.
4. Items still present after recycle failure may be offered for permanent deletion in a second,
   explicit confirmation.
5. Only successfully removed paths are removed from the view.

Permanent deletion is never presented as undoable. If one operation mixes recyclable and permanent
deletion, the status and undo message must describe that limitation.

Deleting entries from inside a ZIP uses archive rewrite behavior rather than the Windows Recycle
Bin.

## Undo history

`UndoHistory` is shared across tabs and exists only for the current process. It records:

- Rename
- Created files and directories
- Paste-created items when tracking is complete
- Recycled physical items

Limits:

- At most 100 history entries
- At most 20,000 tracked paths across history
- Paste tracking may be marked incomplete at a lower filesystem-operation limit

When an entry is evicted, cleared, completed, or disposed, its retained resources must be released.
For Recycle Bin entries this releases held Shell item references.

Undo is preflighted before destructive reversal. It must not remove content that has changed since
creation or overwrite a newly occupied path.

Recycle restore may partially succeed:

- Restored and terminally unavailable tokens are removed from the pending operation.
- Retryable failures, such as an occupied original path, remain available for another Ctrl+Z.
- An operation is removed from history when it succeeds or has no retryable items left.
- `RestoredPaths` tells the UI which locations changed.

Do not add an undo record unless the operation has enough metadata to reverse safely. Use an explicit
unavailable message when an operation invalidates prior assumptions.

## Refresh after mutation

Normal view:

- Reload the current location from its source.

Completed flattened view:

- Apply a targeted add, rename, or remove when the controller can preserve a correct tree.

Active flattened enumeration:

- Stop the worker, apply the mutation, and restart enumeration.

Undo refresh must account for both removed paths and restored paths, which may affect a different tab
or a flattened ancestor.

## Safety invariants

- Never overwrite without the selected conflict policy.
- Never follow a directory reparse point while recursively copying.
- Prevent ZIP extraction and virtual-file paths from escaping their destination root.
- Keep accurate success, error, cancellation, and changed-path collections.
- Permanent deletion always requires distinct confirmation after recycle failure.
- Undo never destroys content whose identity or metadata no longer matches the recorded operation.
- Retained native handles are bounded and released.
