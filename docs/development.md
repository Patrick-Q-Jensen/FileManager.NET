# Development and Operations

## Purpose

Provides the minimal commands and operational facts needed to build, publish, diagnose, and validate
the application.

## Read this when

- Setting up the repository
- Building or publishing
- Changing persistence, diagnostics, dependencies, or release behavior
- Validating a code change

## Related files

- `FileManager.NET.slnx`
- `FileManager.NET\FileManager.NET.csproj`
- `FileManager.NET\Properties\PublishProfiles\FolderProfile.pubxml`
- `FileManager.NET\Program.cs`
- `FileManager.NET\Core\Session\SessionStateService.cs`
- `FileManager.NET\Core\Favorites\FavoritesService.cs`
- `FileManager.NET\Core\Sorting\SortSettingsService.cs`

## Toolchain

- Windows
- .NET 10 SDK
- Terminal.Gui 2.4
- Serilog
- Git executable for optional repository detection

The project is a framework-dependent executable. End users need a compatible .NET 10 runtime.

## Commands

Restore and build:

```powershell
dotnet restore FileManager.NET.slnx
dotnet build FileManager.NET.slnx --no-restore
```

Run:

```powershell
dotnet run --project FileManager.NET\FileManager.NET.csproj
```

Run at a specific directory without restoring saved tabs:

```powershell
dotnet run --project FileManager.NET\FileManager.NET.csproj -- C:\source
```

Publish with the existing profile:

```powershell
dotnet publish FileManager.NET\FileManager.NET.csproj --no-restore -p:PublishProfile=FolderProfile
```

Output:

```text
FileManager.NET\bin\publish\
```

The profile selects Release, `net10.0`, Any CPU, filesystem publishing, and `SelfContained=false`.

## Validation

There is currently no automated test project. For code changes:

1. Run the smallest available build that covers the change.
2. Exercise the affected workflow manually in a terminal.
3. Include partial failure, cancellation, and focus changes when relevant.
4. Check the log for unexpected errors.
5. For native changes, verify resource release during both success and failure.

High-risk manual scenarios:

| Area | Scenarios |
|---|---|
| Input | List focus, tab-header focus, dialog focus, first/last list row |
| Navigation | Physical directory, ZIP directory, inaccessible path, parent restoration |
| Flattening | Large tree, reparse point, inaccessible subtree, cancellation, mutation while active |
| File operations | Conflict, partial failure, cancellation, nested selection, undo retry |
| Recycle Bin | File, directory, occupied restore path, permanent-delete fallback |
| Clipboard | Physical files, virtual files, nested paths, cancellation, invalid relative paths |
| Shutdown | Multiple tabs, active background work, retained Recycle Bin undo items |

Do not add a new lint, test, or build tool solely to validate an unrelated change. When tests are
introduced, document targeted commands here.

## Persistence

Application settings are shared under:

```text
%ProgramData%\FileManager.NET\
```

| File | Contents | Failure behavior |
|---|---|---|
| `session-state.json` | Open physical/archive locations and active tab | Log and start with an empty session |
| `favorites.json` | Up to nine favorite paths in insertion order | Log, notify the UI, and roll back failed mutations |
| `sort-settings.json` | Global `SortMode` | Log and use the in-memory/default mode |

Writes use a temporary file followed by replacement to reduce partial-file risk. Session loading
retains duplicate locations because multiple tabs may intentionally show the same directory.

## Logging

Serilog writes daily rolling files under:

```text
<application-directory>\Logs\
```

Retention is bounded in `Program.ConfigureLogging`. Expected environmental failures are warnings;
unexpected operation failures are errors; process-terminating failures are fatal.

When diagnosing asynchronous behavior, correlate operation messages with:

- Git probe cancellation/location
- Flatten completion or cancellation
- Native Shell and clipboard errors

Key-handling and directory-load timings are instrumented at Debug level, but the default logger
minimum is Information, so those measurements are not written in a normal run.

Do not log clipboard contents, file contents, credentials, or other sensitive data.

## Documentation maintenance

- Keep the root `README.md` as the only documentation routing index.
- Keep each topic focused on stable contracts, invariants, and extension guidance.
- Link exact paths and symbol names rather than copying source code.
- Update documentation in the same change when behavior or ownership changes.
- Put a durable tradeoff in `docs\decisions`; do not use decision records as progress notes.
- Keep temporary plans and benchmark scratch data outside canonical topic documents until adopted.
