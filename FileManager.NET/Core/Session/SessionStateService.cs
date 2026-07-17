using System.Text.Json;
using Serilog;

namespace FileManager.NET.Core.Session;

/// <summary>
/// Persists the directories open at shutdown to
/// <c>%ProgramData%\FileManager.NET\session-state.json</c>.
/// </summary>
internal sealed class SessionStateService
{
    private static readonly string StorageDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FileManager.NET");

    private static readonly string FilePath =
        Path.Combine(StorageDirectory, "session-state.json");

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    internal SessionState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return SessionState.Empty;
            }

            var json = File.ReadAllText(FilePath);
            var saved = JsonSerializer.Deserialize<SessionState>(json);
            return saved is null
                ? SessionState.Empty
                : new SessionState(
                    saved.Directories
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(9)
                        .ToArray(),
                    saved.ActiveTabIndex);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to load session state from {FilePath}", FilePath);
            return SessionState.Empty;
        }
    }

    internal void Save(SessionState state)
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);

            var tmp = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to persist session state to {FilePath}", FilePath);
        }
    }
}

internal sealed record SessionState(IReadOnlyList<string> Directories, int ActiveTabIndex)
{
    internal static SessionState Empty { get; } = new([], 0);
}
