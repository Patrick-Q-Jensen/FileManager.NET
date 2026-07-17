using System.IO.Compression;
using Serilog;

namespace FileManager.NET.Core.FileSystem;

/// <summary>
/// Creates ZIP archives from file-system entries without exposing archive implementation details to the UI.
/// </summary>
internal sealed class ZipArchiveService
{
    public ZipArchiveResult Create(
        IReadOnlyList<FileSystemEntry> entries,
        string destinationDirectory,
        IProgress<ZipArchiveProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var errors = new List<string>();
        var sources = CollectSources(entries, errors);
        if (sources.Count == 0)
        {
            return new ZipArchiveResult(null, 0, errors);
        }

        string archivePath;
        try
        {
            archivePath = GetUniqueArchivePath(destinationDirectory, entries[0].Name);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to determine ZIP archive path in {Directory}", destinationDirectory);
            return new ZipArchiveResult(null, 0, [ex.Message]);
        }

        var filesAdded = 0;
        var filesProcessed = 0;
        try
        {
            using var archiveStream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
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
                        AddFile(archive, source.FullPath, source.EntryName);
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
            TryDeleteIncompleteArchive(archivePath);
            return new ZipArchiveResult(null, filesAdded, [.. errors, ex.Message]);
        }

        return new ZipArchiveResult(archivePath, filesAdded, errors);
    }

    private static ArchiveSources CollectSources(IReadOnlyList<FileSystemEntry> entries, List<string> errors)
    {
        var sources = new ArchiveSources();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                CollectDirectory(entry.FullPath, entry.Name, sources, errors);
            }
            else
            {
                sources.AddFile(entry.FullPath, entry.Name);
            }
        }

        return sources;
    }

    private static void CollectDirectory(string directoryPath, string entryName, ArchiveSources sources, List<string> errors)
    {
        try
        {
            sources.AddDirectory(directoryPath, entryName);
            var pendingDirectories = new Stack<(string FullPath, string EntryName)>();
            pendingDirectories.Push((directoryPath, entryName));

            while (pendingDirectories.TryPop(out var directory))
            {
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
                        try
                        {
                            var attributes = File.GetAttributes(childPath);
                            var childEntryName = $"{directory.EntryName}/{Path.GetFileName(childPath)}";
                            if ((attributes & FileAttributes.Directory) == 0)
                            {
                                sources.AddFile(childPath, childEntryName);
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

    private static void AddFile(ZipArchive archive, string sourcePath, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = entry.Open();
        source.CopyTo(destination);
    }

    private static string GetUniqueArchivePath(string directory, string firstEntryName)
    {
        var baseName = Path.GetFileNameWithoutExtension(firstEntryName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Archive";
        }

        for (var suffix = 1; ; suffix++)
        {
            var name = suffix == 1 ? $"{baseName}.zip" : $"{baseName} ({suffix}).zip";
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }
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
}

internal sealed record ZipArchiveProgress(int FilesProcessed, int TotalFiles);

internal sealed record ZipArchiveResult(string? ArchivePath, int FilesAdded, IReadOnlyList<string> Errors);
