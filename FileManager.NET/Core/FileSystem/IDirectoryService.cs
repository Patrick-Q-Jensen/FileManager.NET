namespace FileManager.NET.Core.FileSystem;

/// <summary>
/// The outcome of loading a directory: its entries plus an optional non-fatal error message
/// (for example when access is denied). Loading never throws for expected I/O conditions.
/// </summary>
internal sealed record DirectoryListing(IReadOnlyList<FileSystemEntry> Entries, string? Error);

/// <summary>
/// Loads directory contents for the file manager. Kept behind an interface so it can be
/// swapped later for asynchronous loading or virtual file systems (archives, remote shares)
/// without affecting callers.
/// </summary>
internal interface IDirectoryService
{
    /// <summary>Loads the entries of <paramref name="path"/> in a single metadata pass.</summary>
    DirectoryListing Load(string path);
}
