using FileManager.NET.Core.FileSystem;

namespace FileManager.NET.Ui;

/// <summary>
/// Converts <see cref="FileSystemEntry"/> values into the single-line text shown in the list.
/// Isolated behind its own type so richer presentation (icons, columns, per-row colors) can be
/// introduced later without changing the view or the core. The list itself is virtualized by
/// <see cref="Terminal.Gui.Views.ListView"/>, so only visible rows are ever drawn.
/// </summary>
internal static class EntryRowFormatter
{
    /// <summary>Formats a single entry. Directories get a trailing slash for quick scanning.</summary>
    public static string Format(FileSystemEntry entry) =>
        entry.IsDirectory ? entry.Name + "/" : entry.Name;
}
