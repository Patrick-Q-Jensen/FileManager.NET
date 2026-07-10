using FileManager.NET.Core.FileSystem;

namespace FileManager.NET.Core.Sorting;

/// <summary>
/// Orders <see cref="FileSystemEntry"/> instances for a given <see cref="SortMode"/>. Directories
/// always sort before files; within each group, the mode picks the data point and direction.
/// Direction is currently fixed per mode (name ascending, date/size descending); a future option
/// could let the user flip it per mode.
/// </summary>
internal static class EntryComparer
{
    public static int Compare(FileSystemEntry a, FileSystemEntry b, SortMode mode)
    {
        if (a.IsDirectory != b.IsDirectory)
        {
            return a.IsDirectory ? -1 : 1;
        }

        return mode switch
        {
            SortMode.Date => b.LastModified.CompareTo(a.LastModified),
            SortMode.Size => b.Size.CompareTo(a.Size),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
    }
}
