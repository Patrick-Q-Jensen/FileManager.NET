namespace FileManager.NET.Core.FileSystem;

internal sealed record RecycledItemToken(
    Guid Id,
    string OriginalPath,
    bool IsDirectory);

internal sealed record RecycleFailure(string Path, string Error);

internal sealed record RecycleResult(
    IReadOnlyList<RecycledItemToken> RecycledItems,
    IReadOnlyList<RecycleFailure> Failures,
    IReadOnlyList<string> UnrecoverablePaths);

internal sealed record RestoreFailure(
    RecycledItemToken Item,
    string Error,
    bool Retryable);

internal sealed record RestoreResult(
    IReadOnlyList<RecycledItemToken> RestoredItems,
    IReadOnlyList<RestoreFailure> Failures,
    IReadOnlyList<string> ChangedPaths);

internal interface IRecycleService : IDisposable
{
    Task<RecycleResult> RecycleAsync(IReadOnlyList<FileSystemEntry> entries);

    Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItemToken> items);

    void Release(IReadOnlyList<RecycledItemToken> items);
}
