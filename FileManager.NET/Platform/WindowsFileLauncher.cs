using System.Diagnostics;
using Serilog;

namespace FileManager.NET.Platform;

/// <summary>
/// Launches files via the Windows shell, so file associations and verbs are honored exactly as
/// in Explorer. The launch is delegated to a detached <c>cmd /c start</c> so the opened program
/// can never write into the console this TUI is drawing on.
/// </summary>
internal sealed class WindowsFileLauncher : IFileLauncher
{
    // Carries the target path to the intermediary out-of-band; see Open.
    private const string TargetVariable = "FILEMANAGER_LAUNCH_TARGET";

    private static readonly string CmdPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "cmd.exe");

    public string? Open(string path, string? arguments = null)
    {
        try
        {
            // Losing the shell's own "file not found" dialog is the price of launching indirectly,
            // so check up front to keep that feedback. One stat per user-initiated open is free.
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Log.Warning("Cannot open {Path}: it no longer exists", path);
                return $"Could not open '{Path.GetFileName(path)}': path not found.";
            }

            // A process started directly by us inherits our console, and programs that write to it
            // (VS Code attaches to the parent console on startup) paint over the TUI. Going through
            // cmd with CreateNoWindow gives the launched program cmd's own hidden console instead.
            // `start` reads its first quoted token as a window title, hence the empty "".
            var startInfo = new ProcessStartInfo
            {
                FileName = CmdPath,
                Arguments = string.IsNullOrWhiteSpace(arguments)
                    ? $"/c start \"\" \"%{TargetVariable}%\""
                    : $"/c start \"\" \"%{TargetVariable}%\" {arguments}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // The path travels via the environment rather than the command line so characters cmd
            // would otherwise interpret (%, &, ^) reach the target verbatim.
            startInfo.Environment[TargetVariable] = path;

            using var process = Process.Start(startInfo);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open {Path}", path);
            return $"Could not open '{Path.GetFileName(path)}': {ex.Message}";
        }
    }
}
