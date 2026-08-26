using Serilog;
using FileManager.NET.Core.FileSystem;

namespace FileManager.NET.Core.Undo;

internal sealed record UndoResult(bool Success, string Message)
{
    public IReadOnlyList<string> RemovedPaths { get; init; } = [];
    public string? MovedFromPath { get; init; }
    public string? MovedToPath { get; init; }
}

/// <summary>Keeps a bounded history of file operations completed during this process.</summary>
internal sealed class UndoHistory
{
    private const int MaxEntries = 100;
    private const int MaxTrackedPaths = 20_000;

    private readonly object _syncRoot = new();
    private readonly List<UndoEntry> _entries = [];
    private int _trackedPaths;
    private bool _undoInProgress;

    public bool HasEntries
    {
        get
        {
            lock (_syncRoot)
            {
                return _entries.Count > 0;
            }
        }
    }

    public void RecordRename(string oldPath, string newPath, bool isDirectory)
    {
        Add(new UndoEntry(
            $"rename {Path.GetFileName(oldPath)}",
            () => UndoRename(oldPath, newPath, isDirectory),
            2));
    }

    public void RecordCreatedFile(
        string path,
        string description)
    {
        try
        {
            var info = new FileInfo(path);
            var operation = new CreatedItemsUndo(
                [new PasteFileChange(
                    path,
                    info.Length,
                    info.CreationTimeUtc,
                    info.LastWriteTimeUtc,
                    info.Attributes)],
                []);
            Add(new UndoEntry(description, operation.Execute, 1));
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            Log.Warning(ex, "Created {Path} but failed to capture undo metadata", path);
            RecordUnavailable("The last create operation cannot be undone because its metadata could not be read.");
        }
    }

    public void RecordCreatedDirectory(string path, string description)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var operation = new CreatedItemsUndo(
                [],
                [new PasteDirectoryChange(path, info.CreationTimeUtc, info.Attributes)]);
            Add(new UndoEntry(description, operation.Execute, 1));
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            Log.Warning(ex, "Created {Path} but failed to capture undo metadata", path);
            RecordUnavailable("The last create operation cannot be undone because its metadata could not be read.");
        }
    }

    public void RecordPaste(PasteResult result)
    {
        if (result.CreatedFiles.Count == 0 && result.CreatedDirectories.Count == 0)
        {
            return;
        }

        var files = result.CreatedFiles.ToArray();
        var directories = result.CreatedDirectories.ToArray();
        var operation = new CreatedItemsUndo(files, directories);
        Add(new UndoEntry(
            "paste",
            operation.Execute,
            files.Length + directories.Length));
    }

    public void RecordUnavailable(string message)
    {
        lock (_syncRoot)
        {
            _entries.Clear();
            _trackedPaths = 0;
            _entries.Add(new UndoEntry(message, null, 0));
        }
    }

    public UndoResult Undo()
    {
        UndoEntry entry;
        lock (_syncRoot)
        {
            if (_undoInProgress)
            {
                return new UndoResult(false, "An undo operation is already running.");
            }

            if (_entries.Count == 0)
            {
                return new UndoResult(false, "Nothing to undo.");
            }

            entry = _entries[^1];
            if (entry.Action is null)
            {
                _entries.RemoveAt(_entries.Count - 1);
                return new UndoResult(false, entry.Description);
            }

            _undoInProgress = true;
        }

        UndoResult result;
        try
        {
            result = entry.Action();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected failure undoing {Description}", entry.Description);
            result = new UndoResult(false, $"Undo failed: {ex.Message}");
        }
        finally
        {
            lock (_syncRoot)
            {
                _undoInProgress = false;
            }
        }

        if (result.Success)
        {
            lock (_syncRoot)
            {
                if (_entries.Count > 0 && ReferenceEquals(_entries[^1], entry))
                {
                    _entries.RemoveAt(_entries.Count - 1);
                    _trackedPaths -= entry.TrackedPaths;
                }
            }
        }

        return result;
    }

    private void Add(UndoEntry entry)
    {
        lock (_syncRoot)
        {
            while (_entries.Count > 0
                   && (_entries.Count == MaxEntries
                       || _trackedPaths + entry.TrackedPaths > MaxTrackedPaths))
            {
                _trackedPaths -= _entries[0].TrackedPaths;
                _entries.RemoveAt(0);
            }

            _entries.Add(entry);
            _trackedPaths += entry.TrackedPaths;
        }
    }

    private static UndoResult UndoRename(string oldPath, string newPath, bool isDirectory)
    {
        try
        {
            var sourceExists = isDirectory ? Directory.Exists(newPath) : File.Exists(newPath);
            if (!sourceExists)
            {
                return new UndoResult(false, $"Undo blocked: '{newPath}' no longer exists.");
            }

            var isCaseOnlyRename = string.Equals(
                oldPath,
                newPath,
                StringComparison.OrdinalIgnoreCase);
            if (!isCaseOnlyRename && (File.Exists(oldPath) || Directory.Exists(oldPath)))
            {
                return new UndoResult(false, $"Undo blocked: '{oldPath}' already exists.");
            }

            if (isDirectory)
            {
                Directory.Move(newPath, oldPath);
            }
            else
            {
                File.Move(newPath, oldPath);
            }

            return new UndoResult(true, $"Undid rename: {Path.GetFileName(newPath)} -> {Path.GetFileName(oldPath)}")
            {
                MovedFromPath = newPath,
                MovedToPath = oldPath,
            };
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            Log.Warning(ex, "Failed to undo rename from {NewPath} to {OldPath}", newPath, oldPath);
            return new UndoResult(false, $"Undo failed: {ex.Message}");
        }
    }

    private sealed class CreatedItemsUndo(
        IReadOnlyList<PasteFileChange> files,
        IReadOnlyList<PasteDirectoryChange> directories)
    {
        private readonly List<PasteFileChange> _files = [.. files];
        private readonly List<PasteDirectoryChange> _directories = directories
            .OrderByDescending(directory => directory.Path.Length)
            .ToList();

        public UndoResult Execute()
        {
            var removedPaths = new List<string>();
            try
            {
                Preflight();

                while (_files.Count > 0)
                {
                    var file = _files[^1];
                    var clearedReadOnly = (file.Attributes & FileAttributes.ReadOnly) != 0;
                    if (clearedReadOnly)
                    {
                        File.SetAttributes(file.Path, file.Attributes & ~FileAttributes.ReadOnly);
                    }

                    try
                    {
                        File.Delete(file.Path);
                    }
                    finally
                    {
                        if (clearedReadOnly && File.Exists(file.Path))
                        {
                            File.SetAttributes(file.Path, file.Attributes);
                        }
                    }

                    removedPaths.Add(file.Path);
                    _files.RemoveAt(_files.Count - 1);
                }

                while (_directories.Count > 0)
                {
                    var directory = _directories[0];
                    Directory.Delete(directory.Path);
                    removedPaths.Add(directory.Path);
                    _directories.RemoveAt(0);
                }

                return new UndoResult(true, "Undid the last file operation.")
                {
                    RemovedPaths = removedPaths,
                };
            }
            catch (UndoBlockedException ex)
            {
                return new UndoResult(false, ex.Message)
                {
                    RemovedPaths = removedPaths,
                };
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                Log.Warning(ex, "Failed to undo creation of file-system items");
                return new UndoResult(false, $"Undo failed: {ex.Message}")
                {
                    RemovedPaths = removedPaths,
                };
            }
        }

        private void Preflight()
        {
            var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in _files)
            {
                expectedPaths.Add(Path.GetFullPath(file.Path));

                var info = new FileInfo(file.Path);
                if (!info.Exists)
                {
                    throw new UndoBlockedException($"Undo blocked: '{file.Path}' no longer exists.");
                }

                if (info.Length != file.Length || info.LastWriteTimeUtc != file.LastWriteTimeUtc)
                {
                    throw new UndoBlockedException(
                        $"Undo blocked: '{file.Path}' has changed since it was created.");
                }

                if (info.CreationTimeUtc != file.CreationTimeUtc
                    || info.Attributes != file.Attributes)
                {
                    throw new UndoBlockedException(
                        $"Undo blocked: '{file.Path}' was replaced or its attributes changed.");
                }
            }

            foreach (var directory in _directories)
            {
                var fullPath = Path.GetFullPath(directory.Path);
                expectedPaths.Add(fullPath);
                if (!Directory.Exists(fullPath))
                {
                    throw new UndoBlockedException($"Undo blocked: '{directory.Path}' no longer exists.");
                }

                var info = new DirectoryInfo(fullPath);
                if (info.CreationTimeUtc != directory.CreationTimeUtc
                    || info.Attributes != directory.Attributes)
                {
                    throw new UndoBlockedException(
                        $"Undo blocked: '{directory.Path}' was replaced or its attributes changed.");
                }
            }

            foreach (var directory in _directories)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    if (!expectedPaths.Contains(Path.GetFullPath(child)))
                    {
                        throw new UndoBlockedException(
                            $"Undo blocked: '{directory.Path}' contains items added after the operation.");
                    }
                }
            }
        }
    }

    private static bool IsExpectedFileSystemException(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException;

    private sealed record UndoEntry(
        string Description,
        Func<UndoResult>? Action,
        int TrackedPaths);

    private sealed class UndoBlockedException(string message) : Exception(message);
}
