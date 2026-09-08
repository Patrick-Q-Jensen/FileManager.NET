# FileManager.NET

FileManager.NET is a Windows terminal file manager built with .NET 10 and
[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui). It supports tabbed navigation, live
filtering, ZIP archives, recursive flattened views, clipboard operations, favorites, Git context,
and session-scoped undo.

## Requirements

- Windows
- .NET 10 SDK to build
- Git on `PATH` for repository detection
- Windows Terminal is recommended for commands launched from the application

## Build and run

```powershell
dotnet build FileManager.NET.slnx
dotnet run --project FileManager.NET\FileManager.NET.csproj
```

Pass a directory to start there instead of restoring the previous session:

```powershell
dotnet run --project FileManager.NET\FileManager.NET.csproj -- C:\source
```

Publish the framework-dependent Release build with the checked-in profile:

```powershell
dotnet publish FileManager.NET\FileManager.NET.csproj -p:PublishProfile=FolderProfile
```

Published files are written to `FileManager.NET\bin\publish\`.

## Project map

| Area | Responsibility |
|---|---|
| `FileManager.NET\Program.cs` | Composition root, application lifetime, logging, and session persistence |
| `FileManager.NET\Core` | UI-independent filesystem, navigation, filtering, sorting, Git, favorites, session, and undo logic |
| `FileManager.NET\Ui` | Terminal.Gui views, input routing, dialogs, refreshes, and operation orchestration |
| `FileManager.NET\Platform` | Windows Shell, clipboard, process-launching, and command integration |

Dependencies flow from `Ui` to `Core` contracts and from the composition root to Windows
implementations. `Core` must not depend on Terminal.Gui.

## Documentation map

Read only the documents relevant to the work being performed:

| When working on... | Read |
|---|---|
| Component boundaries, ownership, threading, or application lifetime | [`docs/architecture.md`](docs/architecture.md) |
| Tabs, focus, keyboard input, Terminal.Gui refreshes, or dialogs | [`docs/ui-and-input.md`](docs/ui-and-input.md) |
| Directory loading, navigation, filtering, sorting, archives, or flattening | [`docs/navigation-and-filesystem.md`](docs/navigation-and-filesystem.md) |
| Copy, paste, rename, create, delete, Recycle Bin, ZIP mutation, or undo | [`docs/file-operations-and-undo.md`](docs/file-operations-and-undo.md) |
| COM, Windows Shell, clipboard formats, launching, or command execution | [`docs/windows-integration.md`](docs/windows-integration.md) |
| Building, publishing, diagnostics, persistence, or validation | [`docs/development.md`](docs/development.md) |
| Durable architectural choices and their tradeoffs | [`docs/decisions`](docs/decisions) |

## Guidance for AI-assisted changes

1. Start with this file and open only the topic documents mapped to the requested change.
2. Inspect the linked code before editing; documentation describes contracts and intent, not every
   implementation detail.
3. Preserve the invariants listed in the relevant topic document.
4. Update documentation when a change alters an invariant, workflow, ownership rule, or user-visible
   behavior.
5. Keep proposals and temporary investigation notes out of canonical documentation. Put durable
   design decisions in `docs\decisions`.

Repository-wide Copilot instructions are in
[`.github\copilot-instructions.md`](.github/copilot-instructions.md).

## License

See [`LICENSE`](LICENSE).
