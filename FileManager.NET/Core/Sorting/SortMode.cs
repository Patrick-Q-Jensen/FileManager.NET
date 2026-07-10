namespace FileManager.NET.Core.Sorting;

/// <summary>
/// The data point used to order entries within a directory listing. Directories are always
/// grouped before files regardless of mode.
/// </summary>
internal enum SortMode
{
    /// <summary>Order by file name, ascending (A-Z).</summary>
    Name,

    /// <summary>Order by last-modified date, descending (newest first).</summary>
    Date,

    /// <summary>Order by file size, descending (largest first).</summary>
    Size,
}
