using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using Serilog;

namespace FileManager.NET.Core.FileSystem;

/// <summary>
/// Creates ZIP archives from file-system entries without exposing archive implementation details to the UI.
/// </summary>
internal sealed class ZipArchiveService
{
    private static readonly string ExtractionRoot =
        Path.Combine(Path.GetTempPath(), "FileManager.NET", "Extracted");

    private readonly object _indexLock = new();
    private readonly Dictionary<string, CachedArchiveIndex> _indexCache =
        new(StringComparer.OrdinalIgnoreCase);

    public ZipArchiveService()
    {
        CleanupOldExtractions();
    }

    public DirectoryListing LoadDirectory(string archivePath, string archiveDirectory)
    {
        try
        {
            var file = new FileInfo(archivePath);
            if (!file.Exists)
            {
                return new DirectoryListing([], $"Archive not found: {archivePath}");
            }

            var index = GetOrBuildIndex(file);
            var directory = archiveDirectory.Trim('/');
            if (!index.Children.TryGetValue(directory, out var children))
            {
                return new DirectoryListing([], $"Archive directory not found: {directory}");
            }

            var entries = new List<FileSystemEntry>(children.Count);
            foreach (var child in children)
            {
                entries.Add(new FileSystemEntry(
                    child.Name,
                    archivePath,
                    child.IsDirectory,
                    child.Size,
                    child.LastModified,
                    child.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
                    child.Path));
            }

            return new DirectoryListing(entries, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Log.Warning(ex, "Failed to browse ZIP archive {ArchivePath}", archivePath);
            return new DirectoryListing([], $"Cannot open archive: {ex.Message}");
        }
    }

    public void Invalidate(string archivePath)
    {
        lock (_indexLock)
        {
            _indexCache.Remove(archivePath);
        }
    }

    public ZipExtractionResult ExtractEntries(IReadOnlyList<FileSystemEntry> entries)
    {
        if (entries.Count == 0)
        {
            return new ZipExtractionResult([], ["Nothing selected to extract."]);
        }

        var archivePath = entries[0].FullPath;
        if (entries.Any(entry => !entry.IsArchiveEntry
                                 || !string.Equals(entry.FullPath, archivePath, StringComparison.OrdinalIgnoreCase)))
        {
            return new ZipExtractionResult([], ["Archive extraction requires entries from one ZIP file."]);
        }

        var destinationRoot = Path.Combine(ExtractionRoot, Guid.NewGuid().ToString("N"));
        var extractedPaths = new List<string>(entries.Count);
        var errors = new List<string>();

        try
        {
            Directory.CreateDirectory(destinationRoot);
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var archiveEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var archiveEntry in archive.Entries)
            {
                if (TryNormalizeArchivePath(archiveEntry.FullName, out var path, out _))
                {
                    archiveEntries.TryAdd(path, archiveEntry);
                }
            }

            foreach (var selected in entries)
            {
                try
                {
                    var selectedPath = selected.ArchiveEntryPath!;
                    var destination = GetSafeExtractionPath(destinationRoot, selected.Name);

                    if (selected.IsDirectory)
                    {
                        Directory.CreateDirectory(destination);
                        ExtractDirectory(archiveEntries, selectedPath, destinationRoot, selected.Name);
                    }
                    else if (archiveEntries.TryGetValue(selectedPath, out var archiveEntry))
                    {
                        ExtractFile(archiveEntry, destination);
                    }
                    else
                    {
                        errors.Add($"{selected.Name}: entry was not found in the archive.");
                        continue;
                    }

                    extractedPaths.Add(destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    errors.Add($"{selected.Name}: {ex.Message}");
                    Log.Warning(ex, "Failed to extract {EntryPath} from ZIP archive {ArchivePath}",
                        selected.ArchiveEntryPath, archivePath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Log.Warning(ex, "Failed to extract entries from ZIP archive {ArchivePath}", archivePath);
            errors.Add(ex.Message);
        }

        return new ZipExtractionResult(extractedPaths, errors);
    }

    public ZipMutationResult DeleteEntries(IReadOnlyList<FileSystemEntry> entries)
    {
        if (!TryGetArchiveSelection(entries, out var archivePath, out var selectedPaths, out var error))
        {
            return new ZipMutationResult(0, [error]);
        }

        return RewriteArchive(
            archivePath,
            (path, _) => selectedPaths.Any(selected =>
                string.Equals(path, selected, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith($"{selected}/", StringComparison.OrdinalIgnoreCase))
                    ? null
                    : path,
            null,
            entries.Count);
    }

    public ZipMutationResult RenameEntry(FileSystemEntry entry, string newName)
    {
        if (!entry.IsArchiveEntry)
        {
            return new ZipMutationResult(0, ["The selected item is not inside a ZIP archive."]);
        }

        var oldPath = entry.ArchiveEntryPath!;
        var parent = GetArchiveParent(oldPath);
        var newPath = parent.Length == 0 ? newName : $"{parent}/{newName}";

        return RewriteArchive(
            entry.FullPath,
            (path, _) =>
            {
                if (string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    return newPath;
                }

                var prefix = $"{oldPath}/";
                return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? $"{newPath}/{path[prefix.Length..]}"
                    : path;
            },
            null,
            1);
    }

    public ZipMutationResult CreateEntry(
        string archivePath,
        string archiveDirectory,
        string name,
        bool isDirectory)
    {
        var entryPath = CombineArchivePath(archiveDirectory, name);
        return RewriteArchive(
            archivePath,
            static (path, _) => path,
            output => output.CreateEntry(
                isDirectory ? EnsureDirectoryEntryName(entryPath) : entryPath,
                CompressionLevel.Optimal),
            1);
    }

    public ZipMutationResult AddEntries(
        string archivePath,
        string archiveDirectory,
        IReadOnlyList<string> sourcePaths,
        ZipConflictResolution conflictResolution,
        IProgress<PasteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var reporter = new ZipPasteProgressReporter(progress);
        reporter.Report(new PasteProgress(
            PasteProgressPhase.Preparing, null, 0, 0, 0, 0));
        cancellationToken.ThrowIfCancellationRequested();
        var listing = LoadDirectory(archivePath, archiveDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        if (listing.Error is not null)
        {
            return new ZipMutationResult(0, [listing.Error]);
        }

        var existingNames = listing.Entries
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var additions = new List<FileSystemEntry>();

        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var trimmedPath = sourcePath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var sourceName = Path.GetFileName(trimmedPath);
                if (sourceName.Length == 0)
                {
                    errors.Add($"{sourcePath}: source name is empty.");
                    continue;
                }

                var isDirectory = Directory.Exists(sourcePath);
                if (!isDirectory && !File.Exists(sourcePath))
                {
                    errors.Add($"{sourcePath}: source no longer exists.");
                    continue;
                }

                var targetName = sourceName;
                if (assignedNames.Contains(targetName))
                {
                    targetName = GetUniqueArchiveEntryName(existingNames, targetName, isDirectory);
                }
                else if (existingNames.Contains(targetName))
                {
                    if (conflictResolution == ZipConflictResolution.Duplicate)
                    {
                        targetName = GetUniqueArchiveEntryName(existingNames, targetName, isDirectory);
                    }
                    else
                    {
                        replacePaths.Add(CombineArchivePath(archiveDirectory, targetName));
                    }
                }

                existingNames.Add(targetName);
                assignedNames.Add(targetName);

                additions.Add(new FileSystemEntry(
                    CombineArchivePath(archiveDirectory, targetName),
                    sourcePath,
                    isDirectory,
                    isDirectory ? 0 : new FileInfo(sourcePath).Length,
                    isDirectory
                        ? Directory.GetLastWriteTime(sourcePath)
                        : File.GetLastWriteTime(sourcePath),
                    isDirectory ? FileAttributes.Directory : FileAttributes.Normal));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                errors.Add($"{sourcePath}: {ex.Message}");
                Log.Warning(ex, "Failed to inspect paste source {SourcePath}", sourcePath);
            }
        }

        if (additions.Count == 0)
        {
            return new ZipMutationResult(0, errors);
        }

        var collectionErrors = new List<string>();
        var sources = CollectSources(
            additions,
            collectionErrors,
            cancellationToken,
            reporter);
        if (collectionErrors.Count > 0)
        {
            return new ZipMutationResult(0, [.. errors, .. collectionErrors]);
        }

        var filesProcessed = 0;
        reporter.Report(new PasteProgress(
            PasteProgressPhase.Rebuilding,
            archivePath,
            0,
            sources.FileCount,
            0,
            0),
            force: true);
        var result = RewriteArchive(
            archivePath,
            (path, _) => replacePaths.Any(replace =>
                string.Equals(path, replace, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith($"{replace}/", StringComparison.OrdinalIgnoreCase))
                    ? null
                    : path,
            output =>
            {
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (source.IsDirectory)
                    {
                        output.CreateEntry(EnsureDirectoryEntryName(source.EntryName));
                    }
                    else
                    {
                        AddFile(output, source.FullPath, source.EntryName, cancellationToken);
                        filesProcessed++;
                        reporter.Report(new PasteProgress(
                            PasteProgressPhase.Copying,
                            source.FullPath,
                            filesProcessed,
                            sources.FileCount,
                            0,
                            0));
                    }
                }
            },
            additions.Count,
            cancellationToken);
        if (!result.Cancelled)
        {
            reporter.Report(new PasteProgress(
                PasteProgressPhase.Finalizing,
                null,
                filesProcessed,
                sources.FileCount,
                0,
                0),
                force: true);
        }

        return new ZipMutationResult(
            result.ItemsChanged,
            [.. errors, .. result.Errors],
            result.Cancelled);
    }

    public ZipArchiveResult Create(
        IReadOnlyList<FileSystemEntry> entries,
        string destinationDirectory,
        string archiveFileName,
        IProgress<ZipArchiveProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveFileName);

        var errors = new List<string>();
        var sources = CollectSources(entries, errors);
        if (sources.Count == 0)
        {
            return new ZipArchiveResult(null, 0, errors);
        }

        string archivePath;
        try
        {
            archivePath = Path.Combine(destinationDirectory, archiveFileName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to determine ZIP archive path in {Directory}", destinationDirectory);
            return new ZipArchiveResult(null, 0, [ex.Message]);
        }

        var filesAdded = 0;
        var filesProcessed = 0;
        var archiveCreated = false;
        try
        {
            using var archiveStream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            archiveCreated = true;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);

            foreach (var source in sources)
            {
                try
                {
                    if (source.IsDirectory)
                    {
                        archive.CreateEntry(EnsureDirectoryEntryName(source.EntryName));
                    }
                    else
                    {
                        AddFile(archive, source.FullPath, source.EntryName, CancellationToken.None);
                        filesAdded++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    errors.Add($"{source.FullPath}: {ex.Message}");
                    Log.Warning(ex, "Failed to add {Path} to ZIP archive {ArchivePath}", source.FullPath, archivePath);
                }
                finally
                {
                    if (!source.IsDirectory)
                    {
                        filesProcessed++;
                        progress?.Report(new ZipArchiveProgress(filesProcessed, sources.FileCount));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Log.Warning(ex, "Failed to create ZIP archive {ArchivePath}", archivePath);
            if (archiveCreated)
            {
                TryDeleteIncompleteArchive(archivePath);
            }

            return new ZipArchiveResult(null, filesAdded, [.. errors, ex.Message]);
        }

        return new ZipArchiveResult(archivePath, filesAdded, errors);
    }

    private static ArchiveSources CollectSources(
        IReadOnlyList<FileSystemEntry> entries,
        List<string> errors,
        CancellationToken cancellationToken = default,
        ZipPasteProgressReporter? reporter = null)
    {
        var sources = new ArchiveSources();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                CollectDirectory(
                    entry.FullPath,
                    entry.Name,
                    sources,
                    errors,
                    cancellationToken,
                    reporter);
            }
            else
            {
                sources.AddFile(entry.FullPath, entry.Name);
                ReportCollectedSources(sources, entry.FullPath, reporter);
            }
        }

        return sources;
    }

    private static void CollectDirectory(
        string directoryPath,
        string entryName,
        ArchiveSources sources,
        List<string> errors,
        CancellationToken cancellationToken,
        ZipPasteProgressReporter? reporter)
    {
        try
        {
            sources.AddDirectory(directoryPath, entryName);
            var pendingDirectories = new Stack<(string FullPath, string EntryName)>();
            pendingDirectories.Push((directoryPath, entryName));

            while (pendingDirectories.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateFileSystemEntries(directory.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{directory.FullPath}: {ex.Message}");
                    Log.Warning(ex, "Failed to enumerate directory for ZIP archive: {Path}", directory.FullPath);
                    continue;
                }

                try
                {
                    foreach (var childPath in children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var attributes = File.GetAttributes(childPath);
                            var childEntryName = $"{directory.EntryName}/{Path.GetFileName(childPath)}";
                            if ((attributes & FileAttributes.Directory) == 0)
                            {
                                sources.AddFile(childPath, childEntryName);
                                ReportCollectedSources(sources, childPath, reporter);
                            }
                            else if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                sources.AddDirectory(childPath, childEntryName);
                            }
                            else
                            {
                                sources.AddDirectory(childPath, childEntryName);
                                pendingDirectories.Push((childPath, childEntryName));
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            errors.Add($"{childPath}: {ex.Message}");
                            Log.Warning(ex, "Failed to inspect path for ZIP archive: {Path}", childPath);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{directory.FullPath}: {ex.Message}");
                    Log.Warning(ex, "Failed to enumerate directory for ZIP archive: {Path}", directory.FullPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            errors.Add($"{directoryPath}: {ex.Message}");
            Log.Warning(ex, "Failed to collect directory for ZIP archive: {Path}", directoryPath);
        }
    }

    private static void ReportCollectedSources(
        ArchiveSources sources,
        string path,
        ZipPasteProgressReporter? reporter) =>
        reporter?.Report(new PasteProgress(
            PasteProgressPhase.Preparing,
            path,
            0,
            sources.FileCount,
            0,
            0));

    private static void AddFile(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = entry.Open();
        CopyStream(source, destination, cancellationToken);
    }

    private static void CopyStream(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = source.Read(buffer, 0, bufferSize);
                if (bytesRead == 0)
                {
                    break;
                }

                destination.Write(buffer, 0, bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string EnsureDirectoryEntryName(string entryName) => $"{entryName.TrimEnd('/')}/";

    private static void TryDeleteIncompleteArchive(string archivePath)
    {
        try
        {
            File.Delete(archivePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remove incomplete ZIP archive {ArchivePath}", archivePath);
        }
    }

    private CachedArchiveIndex GetOrBuildIndex(FileInfo file)
    {
        lock (_indexLock)
        {
            if (_indexCache.TryGetValue(file.FullName, out var cached)
                && cached.Length == file.Length
                && cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                return cached;
            }
        }

        var built = BuildIndex(file);
        lock (_indexLock)
        {
            _indexCache[file.FullName] = built;
        }

        return built;
    }

    private static CachedArchiveIndex BuildIndex(FileInfo file)
    {
        var nodes = new Dictionary<string, IndexedArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (!TryNormalizeArchivePath(entry.FullName, out var path, out var isDirectory))
            {
                skipped++;
                continue;
            }

            var segments = path.Split('/');
            for (var i = 1; i < segments.Length; i++)
            {
                var directoryPath = string.Join('/', segments, 0, i);
                nodes.TryAdd(directoryPath, new IndexedArchiveEntry(
                    directoryPath, true, 0, default));
            }

            var indexed = new IndexedArchiveEntry(
                path,
                isDirectory,
                isDirectory ? 0 : entry.Length,
                entry.LastWriteTime.LocalDateTime);

            if (!nodes.TryAdd(path, indexed) && !isDirectory)
            {
                nodes[path] = indexed;
            }
        }

        var children = new Dictionary<string, List<IndexedArchiveEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = [],
        };

        foreach (var node in nodes.Values)
        {
            var parent = GetArchiveParent(node.Path);
            if (!children.TryGetValue(parent, out var list))
            {
                list = [];
                children[parent] = list;
            }

            list.Add(node);
            if (node.IsDirectory)
            {
                children.TryAdd(node.Path, []);
            }
        }

        if (skipped > 0)
        {
            Log.Warning("Skipped {Count} unsafe or invalid entries while indexing ZIP archive {ArchivePath}",
                skipped, file.FullName);
        }

        return new CachedArchiveIndex(file.Length, file.LastWriteTimeUtc, children);
    }

    private static bool TryNormalizeArchivePath(string rawPath, out string path, out bool isDirectory)
    {
        path = string.Empty;
        isDirectory = false;

        if (string.IsNullOrEmpty(rawPath) || rawPath[0] is '/' or '\\')
        {
            return false;
        }

        isDirectory = rawPath[^1] is '/' or '\\';
        var segments = rawPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOf('\0') >= 0)
            {
                return false;
            }
        }

        path = string.Join('/', segments);
        return true;
    }

    private static string GetArchiveParent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private ZipMutationResult RewriteArchive(
        string archivePath,
        Func<string, bool, string?> mapEntry,
        Action<ZipArchive>? appendEntries,
        int itemsChanged,
        CancellationToken cancellationToken = default)
    {
        var tempPath = $"{archivePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var inputStream = new FileStream(
                       archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var input = new ZipArchive(inputStream, ZipArchiveMode.Read))
            using (var outputStream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create))
            {
                foreach (var inputEntry in input.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isDirectory = inputEntry.FullName.EndsWith('/')
                                      || inputEntry.FullName.EndsWith('\\');
                    var normalized = TryNormalizeArchivePath(
                        inputEntry.FullName, out var safePath, out _)
                            ? safePath
                            : inputEntry.FullName;
                    var mappedPath = mapEntry(normalized, isDirectory);
                    if (mappedPath is null)
                    {
                        continue;
                    }

                    var outputName = isDirectory
                        ? EnsureDirectoryEntryName(mappedPath)
                        : mappedPath;
                    var outputEntry = output.CreateEntry(outputName, CompressionLevel.Optimal);
                    TrySetEntryTimestamp(outputEntry, inputEntry.LastWriteTime);

                    if (!isDirectory)
                    {
                        using var source = inputEntry.Open();
                        using var destination = outputEntry.Open();
                        CopyStream(source, destination, cancellationToken);
                    }
                }

                appendEntries?.Invoke(output);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, archivePath, overwrite: true);
            Invalidate(archivePath);
            return new ZipMutationResult(itemsChanged, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteIncompleteArchive(tempPath);
            return new ZipMutationResult(0, [], true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Log.Warning(ex, "Failed to rewrite ZIP archive {ArchivePath}", archivePath);
            TryDeleteIncompleteArchive(tempPath);
            return new ZipMutationResult(0, [ex.Message]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected failure rewriting ZIP archive {ArchivePath}", archivePath);
            TryDeleteIncompleteArchive(tempPath);
            return new ZipMutationResult(0, [ex.Message]);
        }
    }

    private static bool TryGetArchiveSelection(
        IReadOnlyList<FileSystemEntry> entries,
        out string archivePath,
        out string[] selectedPaths,
        out string error)
    {
        archivePath = entries.Count > 0 ? entries[0].FullPath : string.Empty;
        var selectedArchivePath = archivePath;
        if (entries.Count == 0
            || entries.Any(entry => !entry.IsArchiveEntry
                                    || !string.Equals(entry.FullPath, selectedArchivePath, StringComparison.OrdinalIgnoreCase)))
        {
            selectedPaths = [];
            error = "The selection must contain entries from one ZIP archive.";
            return false;
        }

        selectedPaths = entries.Select(entry => entry.ArchiveEntryPath!).ToArray();
        error = string.Empty;
        return true;
    }

    private static string CombineArchivePath(string directory, string name) =>
        directory.Length == 0 ? name : $"{directory.TrimEnd('/')}/{name}";

    private static string GetUniqueArchiveEntryName(
        ISet<string> existingNames,
        string originalName,
        bool isDirectory)
    {
        var extension = isDirectory ? string.Empty : Path.GetExtension(originalName);
        var baseName = isDirectory ? originalName : Path.GetFileNameWithoutExtension(originalName);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName} ({suffix}){extension}";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static void TrySetEntryTimestamp(ZipArchiveEntry entry, DateTimeOffset timestamp)
    {
        try
        {
            entry.LastWriteTime = timestamp;
        }
        catch (ArgumentOutOfRangeException)
        {
            // ZIP timestamps cannot represent dates outside 1980-2107; keep the default.
        }
    }

    private static void ExtractDirectory(
        IReadOnlyDictionary<string, ZipArchiveEntry> archiveEntries,
        string selectedPath,
        string destinationRoot,
        string selectedName)
    {
        var prefix = $"{selectedPath.TrimEnd('/')}/";
        foreach (var pair in archiveEntries)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = pair.Key[prefix.Length..];
            var destination = GetSafeExtractionPath(
                destinationRoot,
                Path.Combine(selectedName, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (pair.Value.FullName.EndsWith('/') || pair.Value.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
            }
            else
            {
                ExtractFile(pair.Value, destination);
            }
        }
    }

    private static void ExtractFile(ZipArchiveEntry entry, string destination)
    {
        var parent = Path.GetDirectoryName(destination);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        using var source = entry.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(output);
        File.SetLastWriteTime(destination, entry.LastWriteTime.LocalDateTime);
    }

    private static string GetSafeExtractionPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsafe archive path: {relativePath}");
        }

        return fullPath;
    }

    private static void CleanupOldExtractions()
    {
        try
        {
            if (!Directory.Exists(ExtractionRoot))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var directory in Directory.EnumerateDirectories(ExtractionRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "Failed to clean old ZIP extraction directory {Directory}", directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to inspect ZIP extraction directory {Directory}", ExtractionRoot);
        }
    }

    private sealed class ArchiveSources
    {
        private readonly List<ArchiveSource> _items = [];

        public int Count => _items.Count;
        public int FileCount { get; private set; }

        public void AddFile(string fullPath, string entryName)
        {
            _items.Add(new ArchiveSource(fullPath, entryName, false));
            FileCount++;
        }

        public void AddDirectory(string fullPath, string entryName) => _items.Add(new ArchiveSource(fullPath, entryName, true));

        public IEnumerator<ArchiveSource> GetEnumerator() => _items.GetEnumerator();
    }

    private sealed record ArchiveSource(string FullPath, string EntryName, bool IsDirectory);

    private sealed class ZipPasteProgressReporter(IProgress<PasteProgress>? progress)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _lastReportMilliseconds = -100;

        public void Report(PasteProgress update, bool force = false)
        {
            var elapsed = _stopwatch.ElapsedMilliseconds;
            if (!force && elapsed - _lastReportMilliseconds < Interval.TotalMilliseconds)
            {
                return;
            }

            _lastReportMilliseconds = elapsed;
            progress?.Report(update);
        }
    }

    private sealed record IndexedArchiveEntry(
        string Path,
        bool IsDirectory,
        long Size,
        DateTime LastModified)
    {
        public string Name
        {
            get
            {
                var separator = Path.LastIndexOf('/');
                return separator < 0 ? Path : Path[(separator + 1)..];
            }
        }
    }

    private sealed record CachedArchiveIndex(
        long Length,
        DateTime LastWriteTimeUtc,
        Dictionary<string, List<IndexedArchiveEntry>> Children);
}

internal sealed record ZipArchiveProgress(int FilesProcessed, int TotalFiles);

internal sealed record ZipArchiveResult(string? ArchivePath, int FilesAdded, IReadOnlyList<string> Errors);

internal sealed record ZipExtractionResult(IReadOnlyList<string> Paths, IReadOnlyList<string> Errors);

internal enum ZipConflictResolution
{
    Replace,
    Duplicate,
}

internal sealed record ZipMutationResult(
    int ItemsChanged,
    IReadOnlyList<string> Errors,
    bool Cancelled = false);
