using FileManager.NET.Core.FileSystem;

namespace FileManager.NET.Core.Navigation;

/// <summary>
/// Mutable snapshot of navigation state: the current directory, the full (unfiltered) entry
/// list, the live filter query, the filtered view, and any status/error message. Kept
/// UI-agnostic so additional views (preview pane, parent pane, ...) can observe it later.
/// </summary>
internal sealed class NavigationState
{
    public string CurrentDirectory { get; set; } = string.Empty;

    public string Query { get; set; } = string.Empty;

    public string? StatusMessage { get; set; }

    public IReadOnlyList<FileSystemEntry> AllEntries { get; set; } = Array.Empty<FileSystemEntry>();

    public IReadOnlyList<FileSystemEntry> FilteredEntries { get; set; } = Array.Empty<FileSystemEntry>();
}
