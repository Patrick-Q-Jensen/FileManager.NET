namespace FileManager.NET.Platform;

/// <summary>
/// Opens files using the operating system's default handler. Behind an interface even though
/// only a Windows implementation exists today, so other platforms can be added later.
/// </summary>
internal interface IFileLauncher
{
    /// <summary>
    /// Opens <paramref name="path"/> with its associated application. Returns an error message
    /// on failure, or <see langword="null"/> on success.
    /// </summary>
    string? Open(string path);
}
