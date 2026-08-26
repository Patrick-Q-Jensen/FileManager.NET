namespace FileManager.NET.Core.Git;

internal interface IGitRepositoryService
{
    Task<GitRepositoryInfo?> DetectAsync(
        string directory,
        CancellationToken cancellationToken);
}
