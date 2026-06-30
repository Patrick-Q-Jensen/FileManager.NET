using System.Diagnostics;

namespace FileManager.NET.Platform;

/// <summary>
/// Launches files via the Windows shell (ShellExecute), so file associations and verbs are
/// honored exactly as in Explorer.
/// </summary>
internal sealed class WindowsFileLauncher : IFileLauncher
{
    public string? Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not open '{Path.GetFileName(path)}': {ex.Message}";
        }
    }
}
