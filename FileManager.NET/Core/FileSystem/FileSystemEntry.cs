namespace FileManager.NET.Core.FileSystem;

/// <summary>
/// Immutable description of a single file-system entry shown in the file manager.
/// Kept lightweight and UI-agnostic so it can be reused by any view or future feature.
/// </summary>
internal sealed record FileSystemEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime LastModified,
    FileAttributes Attributes)
{
    /// <summary>
    /// Creates the synthetic ".." entry used to navigate up to <paramref name="parentFullPath"/>.
    /// </summary>
    public static FileSystemEntry CreateParent(string parentFullPath) => new(
        Name: "..",
        FullPath: parentFullPath,
        IsDirectory: true,
        Size: 0,
        LastModified: default,
        Attributes: FileAttributes.Directory);
}
