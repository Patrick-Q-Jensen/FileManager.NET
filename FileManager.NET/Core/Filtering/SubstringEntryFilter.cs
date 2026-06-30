using FileManager.NET.Core.FileSystem;

namespace FileManager.NET.Core.Filtering;

/// <summary>
/// Case-insensitive substring matcher over entry names. The hot loop avoids LINQ and uses
/// an ordinal, allocation-free comparison so filtering stays fast on large directories.
/// </summary>
internal sealed class SubstringEntryFilter : IEntryFilter
{
    public IReadOnlyList<FileSystemEntry> Filter(IReadOnlyList<FileSystemEntry> source, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return source;
        }

        var matches = new List<FileSystemEntry>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            var entry = source[i];
            if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(entry);
            }
        }

        return matches;
    }
}
