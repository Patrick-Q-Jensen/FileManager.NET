# Windows Integration

## Purpose

Documents the native Windows boundary: Shell operations, COM lifetime, clipboard formats, process
launching, and command execution.

## Read this when

- Modifying files under `FileManager.NET\Platform`
- Adding P/Invoke or COM interop
- Changing clipboard, Recycle Bin, properties, launching, or command behavior
- Diagnosing apartment-state or native-resource failures

## Related code

- `FileManager.NET\Platform\WindowsFileClipboard.cs`
- `FileManager.NET\Platform\WindowsFileRecycleService.cs`
- `FileManager.NET\Platform\WindowsFileLauncher.cs`
- `FileManager.NET\Platform\WindowsCommandRunner.cs`
- `FileManager.NET\Platform\IFileLauncher.cs`
- `FileManager.NET\Core\FileSystem\IRecycleService.cs`
- `FileManager.NET\Ui\FileManagerWindow.cs`

## Platform boundary

The application targets .NET 10 but is intentionally Windows-specific. Native implementation details
belong in `Platform`; callers should consume focused contracts or result records rather than COM
objects, handles, or HRESULTs.

| Component | Responsibility |
|---|---|
| `WindowsFileLauncher` | Opens a selected path, optionally with arguments |
| `WindowsCommandRunner` | Starts arbitrary commands in the current directory, preferring Windows Terminal |
| `WindowsFileClipboard` | Reads/writes physical file lists and materializes virtual clipboard files |
| `WindowsFileRecycleService` | Recycles and restores physical files with Windows Shell `IFileOperation` |
| `NativeMethods` in the UI | Opens the Windows file properties dialog |

## COM apartment rules

Shell and OLE APIs used here require STA execution:

- `WindowsFileRecycleService` owns a dedicated background STA thread and serial work queue.
- Clipboard OLE work uses `RunOnSta` when the caller is not already in an appropriate apartment.
- Do not move COM work to an arbitrary thread-pool task.
- Do not block the UI thread waiting for work that needs the UI thread to continue.

The Recycle Bin service is application-scoped. It must outlive all undo entries that reference its
tokens, so `FileManagerTabs` disposes `UndoHistory` before `Program` disposes the service.

See
[`decisions/0001-use-windows-shell-for-recycle-bin-undo.md`](decisions/0001-use-windows-shell-for-recycle-bin-undo.md)
for the Recycle Bin design rationale.

## Native ownership

Native ownership must be explicit at every boundary:

- Release COM interface pointers obtained with `Marshal.GetComInterfaceForObject`.
- Release retained `IShellItem` pointers when operations complete, become terminally unavailable,
  are evicted from undo history, or the service shuts down.
- Release COM wrapper objects in `finally`.
- Free global memory when clipboard ownership was not successfully transferred.
- Release `STGMEDIUM` data returned by OLE.
- Delete temporary materialization directories on completion or failure.

Never rely on finalization for correctness. Avoid releasing a pointer after ownership has been
transferred to Windows.

## Recycle Bin operations

`WindowsFileRecycleService` wraps `IFileOperation`:

- Delete uses `FOFX_RECYCLEONDELETE` and `FOF_ALLOWUNDO`.
- A progress sink captures the recycled Shell item for later restoration.
- Restore moves the retained Shell item back to its original parent.
- An occupied original path is retryable; Shell rename-on-collision is detected and reported as a
  terminal partial restore.
- Missing Shell items are terminal failures.
- Each input path is accounted for as recycled, failed, or unrecoverable.

The public service contract uses opaque GUID tokens so native pointers never escape `Platform`.

## Clipboard operations

Physical file copy uses `CF_HDROP`.

Virtual file paste reads:

- `FileGroupDescriptorW` for relative names, sizes, attributes, and timestamps
- `FileContents` as `IStream` or `HGLOBAL`

Before materialization:

- Normalize separators.
- Reject rooted paths and `.` or `..` traversal segments.
- Validate every path segment.
- Resolve the destination and confirm it remains under the staging root.
- Bound arithmetic and propagate cancellation during stream copying.

Do not weaken these checks when supporting another clipboard producer.

## Launching and commands

`WindowsFileLauncher` uses shell execution and returns an error message instead of throwing expected
launch failures.

`WindowsCommandRunner` prefers a new Windows Terminal window and falls back to PowerShell when
necessary. Preserve the requested working directory and quote user-selected paths at the boundary
where command arguments are constructed.

Arbitrary command execution is intentionally explicit user action. Do not execute file names,
clipboard text, or repository metadata as commands without confirmation and correct quoting.

## Native error handling

- Translate expected COM, I/O, access, argument, and unsupported-operation failures into result data.
- Preserve HRESULT-derived detail where it helps the user retry.
- Log unexpected exceptions at error level.
- Do not swallow failures unless cleanup is genuinely best-effort and application state remains
  correct.
- One failed path must not erase successful results for other paths.

## Adding native integration

1. Define a managed contract and ownership model first.
2. Determine apartment and thread-affinity requirements.
3. Document every pointer, handle, memory block, and COM object's owner.
4. Use `try/finally` around native resources.
5. Return structured partial-success data.
6. Validate path and command boundaries before native calls.
7. Add disposal at the same time as allocation.
