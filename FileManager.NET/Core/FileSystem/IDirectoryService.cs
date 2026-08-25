namespace FileManager.NET.Core.FileSystem;

/// <summary>
/// The outcome of loading a directory: its entries plus an optional non-fatal error message
/// (for example when access is denied). Loading never throws for expected I/O conditions.
/// </summary>
internal sealed record DirectoryListing(IReadOnlyList<FileSystemEntry> Entries, string? Error);

/// <summary>The outcome of recursively enumerating a directory tree.</summary>
internal sealed record DirectoryTreeResult(int EntriesFound, int PathsSkipped);

internal enum PasteConflictResolution
{
    None,
    Replace,
    Duplicate,
}

internal enum PasteProgressPhase
{
    Preparing,
    Receiving,
    Copying,
    Rebuilding,
    Finalizing,
}

internal sealed record PasteProgress(
    PasteProgressPhase Phase,
    string? CurrentPath,
    int FilesCompleted,
    int TotalFiles,
    long BytesCopied,
    long TotalBytes);

internal sealed record PasteResult(
    int FilesCopied,
    int TotalFiles,
    int DirectoriesCreated,
    long BytesCopied,
    long TotalBytes,
    bool Cancelled,
    IReadOnlyList<string> Errors)
{
    public bool ItemsChanged => FilesCopied > 0 || DirectoriesCreated > 0;
}

/// <summary>
/// Loads directory contents for the file manager. Kept behind an interface so it can be
/// swapped later for asynchronous loading or virtual file systems (archives, remote shares)
/// without affecting callers.
/// </summary>
internal interface IDirectoryService
{
    /// <summary>Loads the entries of <paramref name="path"/> in a single metadata pass.</summary>
    DirectoryListing Load(string path);

    /// <summary>
    /// Recursively enumerates descendants of <paramref name="path"/> and publishes bounded
    /// batches. Reparse-point directories are listed but not traversed.
    /// </summary>
    DirectoryTreeResult EnumerateTree(
        string path,
        Action<IReadOnlyList<FileSystemEntry>> publishBatch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Copies file-system sources into a directory while reporting progress and preserving
    /// completed files when cancellation is requested.
    /// </summary>
    Task<PasteResult> PasteAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        PasteConflictResolution conflictResolution,
        IProgress<PasteProgress>? progress,
        CancellationToken cancellationToken);
}
