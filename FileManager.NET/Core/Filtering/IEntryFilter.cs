using FileManager.NET.Core.FileSystem;

namespace FileManager.NET.Core.Filtering;

/// <summary>
/// Filters directory entries against a live user query. Behind an interface so the matching
/// strategy (substring, fuzzy/ranked, prefix, ...) can change without touching callers.
/// </summary>
internal interface IEntryFilter
{
    /// <summary>
    /// Returns the entries from <paramref name="source"/> that match <paramref name="query"/>.
    /// An empty query returns all entries.
    /// </summary>
    IReadOnlyList<FileSystemEntry> Filter(IReadOnlyList<FileSystemEntry> source, string query);
}
