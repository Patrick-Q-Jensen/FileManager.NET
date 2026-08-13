namespace FileManager.NET.Core.Navigation;

/// <summary>
/// Identifies either a physical directory or a directory inside a ZIP archive.
/// Archive paths always use forward slashes and never include a leading or trailing slash.
/// </summary>
internal sealed record NavigationLocation(string PhysicalPath, string? ArchiveDirectory = null)
{
    public bool IsArchive => ArchiveDirectory is not null;

    public string DisplayPath => IsArchive
        ? ArchiveDirectory!.Length == 0
            ? $"{PhysicalPath} ::"
            : $"{PhysicalPath} :: {ArchiveDirectory.Replace('/', '\\')}"
        : PhysicalPath;

    public string ContainingDirectory => IsArchive
        ? Path.GetDirectoryName(PhysicalPath) ?? Environment.CurrentDirectory
        : PhysicalPath;

    public static NavigationLocation Directory(string path) => new(path);

    public static NavigationLocation Archive(string archivePath, string archiveDirectory = "") =>
        new(archivePath, archiveDirectory.Trim('/'));
}
