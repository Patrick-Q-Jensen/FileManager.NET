namespace FileManager.NET.Core.Git;

internal sealed record GitRepositoryInfo(
    string WorktreeRoot,
    string Branch,
    string? RemoteRepository);
