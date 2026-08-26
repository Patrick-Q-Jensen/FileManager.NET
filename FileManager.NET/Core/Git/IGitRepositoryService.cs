namespace FileManager.NET.Core.Git;

internal interface IGitRepositoryService
{
    bool IsAvailable { get; }

    Task<GitRepositoryInfo?> DetectAsync(
        string directory,
        CancellationToken cancellationToken);
}
