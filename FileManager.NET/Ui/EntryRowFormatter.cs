using FileManager.NET.Core.FileSystem;

namespace FileManager.NET.Ui;

/// <summary>
/// Converts <see cref="FileSystemEntry"/> values into the single-line text shown in the list:
/// a name column followed by fixed-width, last-modified and size columns. The name column's
/// width adapts to the widest name in the directory currently being rendered (see
/// <see cref="ComputeNameColumnWidth"/>); the date/size columns stay fixed-width. The size is
/// split into a right-aligned number column and a left-aligned unit column so that, e.g., the
/// "3" in "3 B" lines up with the "5" in "5 KB", and the units line up with each other too.
/// Isolated behind its own type so richer presentation (icons, sortable columns, per-row colors)
/// can be introduced later without changing the view or the core. The list itself is virtualized
/// by <see cref="Terminal.Gui.Views.ListView"/>, so only visible rows are ever drawn.
/// </summary>
internal static class EntryRowFormatter
{
    // Floor/ceiling for the adaptive name column so a directory with only short names doesn't
    // collapse the column, and a single absurdly long name doesn't push size/date off-screen.
    private const int MinNameColumnWidth = 20;
    private const int MaxNameColumnWidth = 80;

    private const int DateColumnWidth = 16;
    private const int SizeNumberColumnWidth = 6;
    private const int SizeUnitColumnWidth = 2;

    // Extra spacing between the date and size columns, on top of the normal single-space gap.
    private const int DateSizeGap = 3;

    private const string DateFormat = "yyyy-MM-dd HH:mm";

    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Computes the name column width for a directory listing: the widest displayed name
    /// (accounting for the trailing "/" on directories), clamped to
    /// [<see cref="MinNameColumnWidth"/>, <see cref="MaxNameColumnWidth"/>]. Call this once per
    /// directory/filter change and pass the result to <see cref="Format"/> for every row.
    /// </summary>
    public static int ComputeNameColumnWidth(IReadOnlyList<FileSystemEntry> entries)
    {
        var width = MinNameColumnWidth;
        foreach (var entry in entries)
        {
            var length = entry.IsDirectory ? entry.Name.Length + 1 : entry.Name.Length;
            if (length > width)
            {
                width = length;
            }
        }

        return Math.Min(width, MaxNameColumnWidth);
    }

    /// <summary>Formats a single entry as "name  modified  size" using <paramref name="nameColumnWidth"/>
    /// (see <see cref="ComputeNameColumnWidth"/>). Directories get a trailing slash on the name
    /// and leave the size columns blank.</summary>
    public static string Format(FileSystemEntry entry, int nameColumnWidth)
    {
        var name = entry.IsDirectory ? entry.Name + "/" : entry.Name;
        var modified = entry.LastModified == default ? string.Empty : entry.LastModified.ToString(DateFormat);
        var (sizeNumber, sizeUnit) = entry.IsDirectory ? (string.Empty, string.Empty) : FormatSize(entry.Size);
        var gap = new string(' ', DateSizeGap);

        return $"{FitName(name, nameColumnWidth)} {modified,DateColumnWidth}{gap}{sizeNumber,SizeNumberColumnWidth} {sizeUnit,-SizeUnitColumnWidth}";
    }

    private static string FitName(string name, int nameColumnWidth) =>
        name.Length <= nameColumnWidth
            ? name.PadRight(nameColumnWidth)
            : name[..(nameColumnWidth - 1)] + "…";

    /// <summary>Splits a byte count into a human-readable number and unit, e.g. (128.4, "KB").</summary>
    private static (string Number, string Unit) FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return (bytes.ToString(), "B");
        }

        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < SizeUnits.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return (value.ToString("0.#"), SizeUnits[unitIndex]);
    }
}
