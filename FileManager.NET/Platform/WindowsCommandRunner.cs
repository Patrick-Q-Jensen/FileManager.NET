using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace FileManager.NET.Platform;

/// <summary>
/// Runs a user-typed command line in a PowerShell session of its own. Prefers a new Windows
/// Terminal tab and falls back to a classic console window when Windows Terminal is unavailable.
/// </summary>
internal static class WindowsCommandRunner
{
    // "-w 0" targets the most-recently-used Windows Terminal window, so commands land as a new tab
    // in whatever terminal the user already has open. Windows Terminal creates a window itself when
    // none exists, which covers the first-run case.
    private const string WindowTarget = "0";

    // Windows PowerShell, which ships with the OS, so it is always present.
    private const string Shell = "powershell.exe";

    /// <summary>
    /// Runs <paramref name="command"/> in <paramref name="workingDirectory"/>. Returns an error
    /// message on failure, or <see langword="null"/> on success.
    /// </summary>
    public static string? Run(string command, string workingDirectory)
    {
        if (TryRunInWindowsTerminal(command, workingDirectory))
        {
            return null;
        }

        try
        {
            // -NoExit keeps the console open so output stays visible and interactive tools still work.
            Process.Start(new ProcessStartInfo
            {
                FileName = Shell,
                Arguments = "-NoExit -Command " + command,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
            });

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to run {Command} in {Directory}", command, workingDirectory);
            return $"Run failed: {ex.Message}";
        }
    }

    private static bool TryRunInWindowsTerminal(string command, string workingDirectory)
    {
        // Windows Terminal treats ';' as its own subcommand separator, so it must be escaped even
        // when the command arrives as a single argument.
        var escaped = command.Replace(";", "\\;");

        var info = new ProcessStartInfo
        {
            FileName = "wt.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList quotes each argument correctly; a hand-built Arguments string would break on
        // directories ending in a backslash, where \" reads as an escaped quote.
        info.ArgumentList.Add("-w");
        info.ArgumentList.Add(WindowTarget);
        info.ArgumentList.Add("new-tab");
        info.ArgumentList.Add("--title");
        info.ArgumentList.Add(escaped);
        info.ArgumentList.Add("-d");
        info.ArgumentList.Add(workingDirectory);
        info.ArgumentList.Add(Shell);
        info.ArgumentList.Add("-NoExit");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(escaped);

        try
        {
            Process.Start(info);
            return true;
        }
        catch (Win32Exception ex)
        {
            // Windows Terminal is not installed on this machine; the caller falls back to a console.
            Log.Warning(ex, "Windows Terminal (wt.exe) unavailable, falling back to a console window");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to start Windows Terminal for {Command}", command);
            return false;
        }
    }
}
