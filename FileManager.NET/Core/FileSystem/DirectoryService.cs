using Serilog;

namespace FileManager.NET.Core.FileSystem;

/// <summary>
/// Default <see cref="IDirectoryService"/> that enumerates the file system in a single
/// metadata pass and returns directories first, then files, each sorted case-insensitively.
/// </summary>
internal sealed class DirectoryService : IDirectoryService
{
    private const int TreeBatchSize = 256;

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
}
