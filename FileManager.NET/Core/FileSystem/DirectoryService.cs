using System.Buffers;
using System.Diagnostics;
using Serilog;

namespace FileManager.NET.Core.FileSystem;

/// <summary>
/// Default <see cref="IDirectoryService"/> that enumerates the file system in a single
/// metadata pass and returns directories first, then files, each sorted case-insensitively.
/// </summary>
internal sealed class DirectoryService : IDirectoryService
{
    private const int TreeBatchSize = 256;
    private const int CopyBufferSize = 1024 * 1024;
    private const int MaxTrackedUndoPaths = 10_000;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    public DirectoryListing Load(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            var entries = new List<FileSystemEntry>();

            // EnumerateFileSystemInfos returns metadata cached from the OS enumeration,
            // so reading Attributes/Length/LastWriteTime below does not cost extra stat calls.
            foreach (var info in directory.EnumerateFileSystemInfos())
            {
                var isDirectory = (info.Attributes & FileAttributes.Directory) == FileAttributes.Directory;

                long size = 0;
                if (!isDirectory && info is FileInfo file)
                {
                    try
                    {
                        size = file.Length;
                    }
                    catch (IOException ex)
                    {
                        Log.Warning(ex, "Failed to read file size for {Path}", info.FullName);
                        size = 0;
                    }
                }

                entries.Add(new FileSystemEntry(
                    info.Name,
                    info.FullName,
                    isDirectory,
                    size,
                    info.LastWriteTime,
                    info.Attributes));
            }

            // Entries are returned unsorted; NavigationController applies the active SortMode
            // (local or global) after loading, so ordering is never done twice.
            return new DirectoryListing(entries, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied while listing directory {Path}", path);
            return new DirectoryListing(Array.Empty<FileSystemEntry>(), $"Access denied: {path}");
        }
        catch (DirectoryNotFoundException ex)
        {
            Log.Warning(ex, "Directory not found: {Path}", path);
            return new DirectoryListing(Array.Empty<FileSystemEntry>(), $"Not found: {path}");
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "I/O error while listing directory {Path}", path);
            return new DirectoryListing(Array.Empty<FileSystemEntry>(), ex.Message);
        }
    }

    public DirectoryTreeResult EnumerateTree(
        string path,
        Action<IReadOnlyList<FileSystemEntry>> publishBatch,
        CancellationToken cancellationToken)
    {
        var pending = new List<FileSystemEntry>(TreeBatchSize);
        var directories = new Stack<(DirectoryInfo Directory, string? RelativePath)>();
        directories.Push((new DirectoryInfo(path), null));

        var entriesFound = 0;
        var pathsSkipped = 0;

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = directories.Pop();

            try
            {
                foreach (var info in current.Directory.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var attributes = info.Attributes;
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        var size = !isDirectory && info is FileInfo file ? file.Length : 0;

                        pending.Add(new FileSystemEntry(
                            info.Name,
                            info.FullName,
                            isDirectory,
                            size,
                            info.LastWriteTime,
                            attributes,
                            RelativeParentPath: current.RelativePath));
                        entriesFound++;

                        if (isDirectory && (attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            var relativePath = current.RelativePath is null
                                ? info.Name
                                : $"{current.RelativePath}/{info.Name}";
                            directories.Push((new DirectoryInfo(info.FullName), relativePath));
                        }

                        if (pending.Count == TreeBatchSize)
                        {
                            publishBatch(pending.ToArray());
                            pending.Clear();
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        pathsSkipped++;
                        Log.Warning(ex, "Failed to read file-system entry {Path}", info.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pathsSkipped++;
                Log.Warning(ex, "Failed to enumerate directory {Path}", current.Directory.FullName);
            }
        }

        if (pending.Count > 0)
        {
            publishBatch(pending.ToArray());
        }

        return new DirectoryTreeResult(entriesFound, pathsSkipped);
    }

    public Task<PasteResult> PasteAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        PasteConflictResolution conflictResolution,
        IProgress<PasteProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => PasteCoreAsync(
                sourcePaths,
                destinationDirectory,
                conflictResolution,
                progress,
                cancellationToken),
            CancellationToken.None);

    private static async Task<PasteResult> PasteCoreAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        PasteConflictResolution conflictResolution,
        IProgress<PasteProgress>? progress,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var reporter = new PasteProgressReporter(progress);
        var plan = new PastePlan();
        var filesCopied = 0;
        var directoriesCreated = 0;
        var createdFiles = new List<PasteFileChange>();
        var createdDirectories = new List<PasteDirectoryChange>();
        var replacedExisting = false;
        var undoTrackingComplete = true;
        long bytesCopied = 0;

        try
        {
            reporter.Report(PasteProgressPhase.Preparing, null, 0, 0, 0, 0, force: true);
            BuildPastePlan(
                sourcePaths,
                destinationDirectory,
                conflictResolution,
                plan,
                errors,
                reporter,
                cancellationToken);

            reporter.Report(
                PasteProgressPhase.Copying,
                null,
                0,
                plan.Files.Count,
                0,
                plan.TotalBytes,
                force: true);

            foreach (var directory in plan.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                        directoriesCreated++;
                        if (createdFiles.Count + createdDirectories.Count < MaxTrackedUndoPaths)
                        {
                            if (TryCaptureCreatedDirectory(directory, out var createdDirectory))
                            {
                                createdDirectories.Add(createdDirectory);
                            }
                            else
                            {
                                undoTrackingComplete = false;
                            }
                        }
                        else
                        {
                            undoTrackingComplete = false;
                        }
                    }
                }
                catch (Exception ex) when (IsExpectedFileSystemException(ex))
                {
                    errors.Add($"{directory}: {ex.Message}");
                    Log.Warning(ex, "Failed to create paste destination directory {Directory}", directory);
                }
            }

            foreach (var file in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var committedBytes = bytesCopied;
                    var fileBytes = await CopyFileAsync(
                        file,
                        currentFileBytes => reporter.Report(
                            PasteProgressPhase.Copying,
                            file.SourcePath,
                            filesCopied,
                            plan.Files.Count,
                            committedBytes + currentFileBytes,
                            plan.TotalBytes),
                        cancellationToken).ConfigureAwait(false);

                    bytesCopied += fileBytes;
                    filesCopied++;
                    if (file.Overwrite)
                    {
                        replacedExisting = true;
                    }
                    else
                    {
                        if (createdFiles.Count + createdDirectories.Count < MaxTrackedUndoPaths)
                        {
                            if (TryCaptureCreatedFile(file.DestinationPath, out var createdFile))
                            {
                                createdFiles.Add(createdFile);
                            }
                            else
                            {
                                undoTrackingComplete = false;
                            }
                        }
                        else
                        {
                            undoTrackingComplete = false;
                        }
                    }
                    reporter.Report(
                        PasteProgressPhase.Copying,
                        file.SourcePath,
                        filesCopied,
                        plan.Files.Count,
                        bytesCopied,
                        plan.TotalBytes);
                }
                catch (Exception ex) when (IsExpectedFileSystemException(ex))
                {
                    errors.Add($"{file.SourcePath}: {ex.Message}");
                    Log.Warning(ex, "Failed to paste {Source} to {Destination}",
                        file.SourcePath, file.DestinationPath);
                }
            }

            reporter.Report(
                PasteProgressPhase.Finalizing,
                null,
                filesCopied,
                plan.Files.Count,
                bytesCopied,
                plan.TotalBytes,
                force: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PasteResult(
                filesCopied,
                plan.Files.Count,
                directoriesCreated,
                bytesCopied,
                plan.TotalBytes,
                true,
                errors)
            {
                CreatedFiles = createdFiles,
                CreatedDirectories = createdDirectories,
                ReplacedExisting = replacedExisting,
                UndoTrackingComplete = undoTrackingComplete,
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected paste failure in {Destination}", destinationDirectory);
            errors.Add(ex.Message);
        }

        return new PasteResult(
            filesCopied,
            plan.Files.Count,
            directoriesCreated,
            bytesCopied,
            plan.TotalBytes,
            false,
            errors)
        {
            CreatedFiles = createdFiles,
            CreatedDirectories = createdDirectories,
            ReplacedExisting = replacedExisting,
            UndoTrackingComplete = undoTrackingComplete,
        };
    }

    private static void BuildPastePlan(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        PasteConflictResolution conflictResolution,
        PastePlan plan,
        List<string> errors,
        PasteProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var trimmedSource = sourcePath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var sourceName = Path.GetFileName(trimmedSource);
                if (sourceName.Length == 0)
                {
                    errors.Add($"{sourcePath}: source name is empty.");
                    continue;
                }

                var attributes = File.GetAttributes(trimmedSource);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var destinationPath = Path.Combine(destinationDirectory, sourceName);
                var destinationExists = File.Exists(destinationPath) || Directory.Exists(destinationPath);

                if (destinationExists)
                {
                    if (conflictResolution == PasteConflictResolution.Duplicate)
                    {
                        destinationPath = GetUniqueDestinationPath(
                            destinationDirectory,
                            sourceName,
                            isDirectory);
                        destinationExists = false;
                    }
                    else if (conflictResolution == PasteConflictResolution.None)
                    {
                        errors.Add($"{sourcePath}: '{sourceName}' already exists.");
                        continue;
                    }
                }

                if (isDirectory)
                {
                    CollectPasteDirectory(
                        trimmedSource,
                        destinationPath,
                        overwriteFiles: destinationExists
                                        && conflictResolution == PasteConflictResolution.Replace,
                        plan,
                        errors,
                        reporter,
                        cancellationToken);
                }
                else
                {
                    AddPasteFile(
                        new FileInfo(trimmedSource),
                        destinationPath,
                        overwrite: destinationExists
                                   && conflictResolution == PasteConflictResolution.Replace,
                        plan);
                    reporter.Report(
                        PasteProgressPhase.Preparing,
                        trimmedSource,
                        0,
                        plan.Files.Count,
                        0,
                        plan.TotalBytes);
                }
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                errors.Add($"{sourcePath}: {ex.Message}");
                Log.Warning(ex, "Failed to inspect paste source {Source}", sourcePath);
            }
        }
    }

    private static void CollectPasteDirectory(
        string sourceRoot,
        string destinationRoot,
        bool overwriteFiles,
        PastePlan plan,
        List<string> errors,
        PasteProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        plan.Directories.Add(destinationRoot);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            reporter.Report(
                PasteProgressPhase.Preparing,
                directory.Source,
                0,
                plan.Files.Count,
                0,
                plan.TotalBytes);

            try
            {
                foreach (var info in new DirectoryInfo(directory.Source).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = info.Attributes;
                        var destinationPath = Path.Combine(directory.Destination, info.Name);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            plan.Directories.Add(destinationPath);
                            if ((attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                pending.Push((info.FullName, destinationPath));
                            }
                            else
                            {
                                errors.Add($"{info.FullName}: reparse-point directories are not traversed.");
                                Log.Warning("Skipped reparse-point directory while pasting {Path}", info.FullName);
                            }
                        }
                        else if (info is FileInfo file)
                        {
                            AddPasteFile(
                                file,
                                destinationPath,
                                overwriteFiles && File.Exists(destinationPath),
                                plan);
                            reporter.Report(
                                PasteProgressPhase.Preparing,
                                file.FullName,
                                0,
                                plan.Files.Count,
                                0,
                                plan.TotalBytes);
                        }
                    }
                    catch (Exception ex) when (IsExpectedFileSystemException(ex))
                    {
                        errors.Add($"{info.FullName}: {ex.Message}");
                        Log.Warning(ex, "Failed to inspect paste source {Source}", info.FullName);
                    }
                }
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                errors.Add($"{directory.Source}: {ex.Message}");
                Log.Warning(ex, "Failed to enumerate paste source directory {Directory}", directory.Source);
            }
        }
    }

    private static void AddPasteFile(
        FileInfo source,
        string destinationPath,
        bool overwrite,
        PastePlan plan)
    {
        var length = source.Length;
        plan.Files.Add(new PasteFile(
            source.FullName,
            destinationPath,
            length,
            source.LastWriteTimeUtc,
            source.Attributes,
            overwrite));
        plan.TotalBytes = AddWithoutOverflow(plan.TotalBytes, length);
    }

    private static async Task<long> CopyFileAsync(
        PasteFile file,
        Action<long> reportBytes,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(file.DestinationPath)
            ?? throw new IOException($"Destination has no parent directory: {file.DestinationPath}");
        Directory.CreateDirectory(destinationDirectory);

        var tempPath = Path.Combine(destinationDirectory, $".fm-{Guid.NewGuid():N}.tmp");
        var committed = false;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long bytesWritten = 0;

        try
        {
            await using (var source = new FileStream(
                             file.SourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytesRead = await source.ReadAsync(
                        buffer.AsMemory(0, CopyBufferSize),
                        cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        cancellationToken).ConfigureAwait(false);
                    bytesWritten += bytesRead;
                    reportBytes(bytesWritten);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, file.DestinationPath, file.Overwrite);
            committed = true;
            TryApplyCopiedMetadata(file);
            return bytesWritten;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!committed)
            {
                TryDeleteTemporaryFile(tempPath);
            }
        }
    }

    private static void TryApplyCopiedMetadata(PasteFile file)
    {
        try
        {
            File.SetLastWriteTimeUtc(file.DestinationPath, file.LastWriteTimeUtc);
            File.SetAttributes(file.DestinationPath, file.Attributes);
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            Log.Warning(ex, "Pasted {Path} but failed to preserve its metadata", file.DestinationPath);
        }
    }

    private static bool TryCaptureCreatedFile(string path, out PasteFileChange change)
    {
        try
        {
            var info = new FileInfo(path);
            change = new PasteFileChange(
                path,
                info.Length,
                info.CreationTimeUtc,
                info.LastWriteTimeUtc,
                info.Attributes);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            Log.Warning(ex, "Pasted {Path} but failed to capture undo metadata", path);
            change = null!;
            return false;
        }
    }

    private static bool TryCaptureCreatedDirectory(
        string path,
        out PasteDirectoryChange change)
    {
        try
        {
            var info = new DirectoryInfo(path);
            change = new PasteDirectoryChange(
                path,
                info.CreationTimeUtc,
                info.Attributes);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            Log.Warning(ex, "Created pasted directory {Path} but failed to capture undo metadata", path);
            change = null!;
            return false;
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            Log.Warning(ex, "Failed to remove incomplete paste file {Path}", path);
        }
    }

    private static string GetUniqueDestinationPath(
        string destinationDirectory,
        string name,
        bool isDirectory)
    {
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        var baseName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);

        for (var suffix = 2; ; suffix++)
        {
            var candidateName = $"{baseName} ({suffix}){extension}";
            var candidate = Path.Combine(destinationDirectory, candidateName);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static long AddWithoutOverflow(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static bool IsExpectedFileSystemException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    private sealed class PastePlan
    {
        public List<string> Directories { get; } = [];
        public List<PasteFile> Files { get; } = [];
        public long TotalBytes { get; set; }
    }

    private sealed record PasteFile(
        string SourcePath,
        string DestinationPath,
        long Length,
        DateTime LastWriteTimeUtc,
        FileAttributes Attributes,
        bool Overwrite);

    private sealed class PasteProgressReporter(IProgress<PasteProgress>? progress)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _lastReportMilliseconds = -100;

        public void Report(
            PasteProgressPhase phase,
            string? currentPath,
            int filesCompleted,
            int totalFiles,
            long bytesCopied,
            long totalBytes,
            bool force = false)
        {
            var elapsed = _stopwatch.ElapsedMilliseconds;
            if (!force && elapsed - _lastReportMilliseconds < ProgressInterval.TotalMilliseconds)
            {
                return;
            }

            _lastReportMilliseconds = elapsed;
            progress?.Report(new PasteProgress(
                phase,
                currentPath,
                filesCompleted,
                totalFiles,
                bytesCopied,
                totalBytes));
        }
    }
}
