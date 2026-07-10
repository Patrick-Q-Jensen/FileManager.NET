namespace FileManager.NET.Core.FileSystem;

/// <summary>
/// Default <see cref="IDirectoryService"/> that enumerates the file system in a single
/// metadata pass and returns directories first, then files, each sorted case-insensitively.
/// </summary>
internal sealed class DirectoryService : IDirectoryService
{
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
                    catch (IOException)
                    {
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
        catch (UnauthorizedAccessException)
        {
            return new DirectoryListing(Array.Empty<FileSystemEntry>(), $"Access denied: {path}");
        }
        catch (DirectoryNotFoundException)
        {
            return new DirectoryListing(Array.Empty<FileSystemEntry>(), $"Not found: {path}");
        }
        catch (IOException ex)
        {
            return new DirectoryListing(Array.Empty<FileSystemEntry>(), ex.Message);
        }
    }
}
