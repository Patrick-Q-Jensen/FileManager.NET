# 0001: Use Windows Shell for Recycle Bin Undo

- Status: Accepted
- Date: 2026-09-08

## Context

Permanent filesystem deletion cannot provide a safe general-purpose undo. Moving items to the
Windows Recycle Bin preserves user expectations, but restoring the correct item is ambiguous if the
application later searches the Recycle Bin by original path or display name.

Windows Shell `IFileOperation` can return the recycled `IShellItem` through an operation progress
sink. Retaining that item gives the current process an exact restore target.

Shell file operations and callbacks require careful COM lifetime management and STA execution.

## Decision

Use `IFileOperation` on a dedicated application-scoped STA worker for physical delete and restore.

- `IRecycleService` exposes managed request/result records.
- A successful recycle returns an opaque GUID token.
- `WindowsFileRecycleService` maps that token to a retained recycled `IShellItem`.
- `UndoHistory` owns tokens while their delete operation remains undoable.
- Restore moves the retained Shell item to its original parent and name.
- Discarding or completing undo releases the retained native item.
- Recycle failures may be offered for permanent deletion only through a second explicit
  confirmation.

ZIP entry deletion remains an archive rewrite and does not use this service.

## Consequences

Benefits:

- Undo targets the exact recycled item.
- The application uses native Windows Recycle Bin semantics.
- Per-item partial success and retryable restore failures can be represented.
- Native pointers do not escape the platform implementation.

Costs:

- The application remains Windows-specific.
- A dedicated STA thread and serialized work queue are required.
- Undo items retain native Shell references until restored, evicted, or shutdown.
- Recycle Bin undo is process-scoped; tokens are not persisted between launches.
- Shell success without a recoverable item must be reported as permanently unrecoverable.

## Required invariants

- The recycle service outlives `UndoHistory`.
- Every queued input is accounted for as recycled, failed, or unrecoverable.
- Every retained COM pointer has one release path.
- Retryable restore failures remain in history; terminal failures do not.
- An occupied original path is never overwritten silently.
- Permanent deletion is clearly distinguished from recyclable deletion.
