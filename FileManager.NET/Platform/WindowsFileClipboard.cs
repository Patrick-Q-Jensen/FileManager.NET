using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using FileManager.NET.Core.FileSystem;
using Serilog;

namespace FileManager.NET.Platform;

/// <summary>
/// Provides access to the Windows clipboard for file-system items using the CF_HDROP format
/// and pastes virtual Shell files such as files copied through Remote Desktop. All operations
/// are dispatched on a dedicated STA thread as required by the Windows clipboard API.
/// </summary>
internal static class WindowsFileClipboard
{
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint FD_ATTRIBUTES = 0x00000004;
    private const uint FD_WRITESTIME = 0x00000020;
    private const uint FD_FILESIZE = 0x00000040;
    private const string FileGroupDescriptorFormat = "FileGroupDescriptorW";
    private const string FileContentsFormat = "FileContents";
    private static readonly Lazy<uint> FileGroupDescriptorFormatId =
        new(() => RegisterClipboardFormat(FileGroupDescriptorFormat));
    private static readonly Lazy<uint> FileContentsFormatId =
        new(() => RegisterClipboardFormat(FileContentsFormat));

    [StructLayout(LayoutKind.Sequential)]
    private struct DROPFILES
    {
        public uint pFiles;  // Byte offset of the file list from the start of this structure.
        public int pt_x;     // Unused drop point; kept zero.
        public int pt_y;
        public int fNC;      // 0 = client-area drop.
        public int fWide;    // 1 = file names are Unicode strings.
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FILEDESCRIPTORW
    {
        public uint dwFlags;
        public Guid clsid;
        public int sizel_cx;
        public int sizel_cy;
        public int pointl_x;
        public int pointl_y;
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? cFileName;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, char[]? lpszFile, uint cch);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out IDataObject dataObject);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    /// <summary>
    /// Puts the given file-system paths into the clipboard as a CF_HDROP file drop list so
    /// that receiving applications (Explorer, etc.) can paste them. Returns <c>true</c> on success.
    /// </summary>
    public static bool TrySetFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return false;

        return RunOnSta(() => TrySetFilesCore(paths));
    }

    private static bool TrySetFilesCore(IReadOnlyList<string> paths)
    {
        var fileList = BuildFileListChars(paths);
        int headerSize = Marshal.SizeOf<DROPFILES>();
        nuint totalBytes = (nuint)(headerSize + fileList.Length * sizeof(char));

        var hGlobal = GlobalAlloc(GMEM_MOVEABLE, totalBytes);
        if (hGlobal == IntPtr.Zero)
            return false;

        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }

        Marshal.StructureToPtr(
            new DROPFILES { pFiles = (uint)headerSize, fWide = 1 },
            ptr,
            fDeleteOld: false);

        Marshal.Copy(fileList, 0, IntPtr.Add(ptr, headerSize), fileList.Length);

        GlobalUnlock(hGlobal);

        if (!OpenClipboard(IntPtr.Zero))
        {
            GlobalFree(hGlobal);
            return false;
        }

        bool success;
        try
        {
            EmptyClipboard();
            // On success Windows takes ownership of hGlobal; do not free it.
            success = SetClipboardData(CF_HDROP, hGlobal) != IntPtr.Zero;
        }
        finally
        {
            CloseClipboard();
        }

        if (!success)
            GlobalFree(hGlobal);

        return success;
    }

    /// <summary>
    /// Reads file-system paths from the clipboard CF_HDROP format. Returns <c>true</c> when at
    /// least one path was found; <paramref name="paths"/> is always set (empty on failure).
    /// Returns <c>false</c> when the clipboard holds non-file data such as plain text, so callers
    /// can safely distinguish "clipboard has files" from "clipboard has something else".
    /// </summary>
    public static bool TryGetFiles(out IReadOnlyList<string> paths)
    {
        IReadOnlyList<string> captured = [];
        bool found = RunOnSta(() =>
        {
            bool ok = TryGetFilesCore(out var list);
            captured = list;
            return ok;
        });
        paths = captured;
        return found;
    }

    private static bool TryGetFilesCore(out IReadOnlyList<string> paths)
    {
        paths = [];

        if (!IsClipboardFormatAvailable(CF_HDROP))
            return false;

        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            var hDrop = GetClipboardData(CF_HDROP);
            if (hDrop == IntPtr.Zero)
                return false;

            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0)
                return false;

            var result = new List<string>((int)count);
            var buffer = new char[32768]; // Large enough for extended-length paths.
            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFile(hDrop, i, buffer, (uint)buffer.Length);
                if (len > 0)
                    result.Add(new string(buffer, 0, (int)len));
            }

            paths = result;
            return result.Count > 0;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool HasVirtualFiles() =>
        OperatingSystem.IsWindows()
        && FileGroupDescriptorFormatId.Value != 0
        && FileContentsFormatId.Value != 0
        && IsClipboardFormatAvailable(FileGroupDescriptorFormatId.Value);

    public static bool TryGetVirtualFileManifest(
        out VirtualFileManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;
        if (!HasVirtualFiles())
        {
            return false;
        }

        try
        {
            manifest = RunOnSta(() => WithOleClipboard(ReadVirtualFileManifest));
            return true;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Failed to read virtual clipboard file descriptors");
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected failure reading virtual clipboard file descriptors");
            error = ex.Message;
            return false;
        }
    }

    public static VirtualFileMaterializationResult MaterializeVirtualFiles(
        VirtualFileManifest manifest,
        IProgress<PasteProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return RunOnSta(() => WithOleClipboard(
                clipboard => MaterializeVirtualFilesCore(
                    clipboard,
                    manifest,
                    progress,
                    cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return VirtualFileMaterializationResult.CancelledResult(manifest.FileCount, manifest.TotalBytes);
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Failed to receive virtual clipboard files");
            return VirtualFileMaterializationResult.Failed(manifest.FileCount, manifest.TotalBytes, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected failure receiving virtual clipboard files");
            return VirtualFileMaterializationResult.Failed(manifest.FileCount, manifest.TotalBytes, ex.Message);
        }
    }

    private static T WithOleClipboard<T>(Func<IDataObject, T> action)
    {
        var initialization = OleInitialize(IntPtr.Zero);
        if (initialization < 0)
        {
            Marshal.ThrowExceptionForHR(initialization);
        }

        IDataObject? clipboard = null;
        try
        {
            var result = OleGetClipboard(out clipboard);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            return action(clipboard);
        }
        finally
        {
            ReleaseComObject(clipboard);
            OleUninitialize();
        }
    }

    private static VirtualFileManifest ReadVirtualFileManifest(IDataObject clipboard)
    {
        var clipboardSequenceNumber = GetClipboardSequenceNumber();
        var medium = default(STGMEDIUM);
        var hasMedium = false;
        try
        {
            var format = CreateFormat(FileGroupDescriptorFormatId.Value, -1, TYMED.TYMED_HGLOBAL);
            clipboard.GetData(ref format, out medium);
            hasMedium = true;

            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero)
            {
                throw new InvalidDataException("The clipboard returned an invalid file descriptor medium.");
            }

            var descriptorPointer = GlobalLock(medium.unionmember);
            if (descriptorPointer == IntPtr.Zero)
            {
                throw new IOException("The clipboard file descriptors could not be locked.");
            }

            try
            {
                var count = unchecked((uint)Marshal.ReadInt32(descriptorPointer));
                if (count == 0 || count > 100_000)
                {
                    throw new InvalidDataException($"The clipboard reported an invalid file count ({count}).");
                }

                var descriptorSize = Marshal.SizeOf<FILEDESCRIPTORW>();
                var requiredSize = checked(4L + (long)count * descriptorSize);
                var availableSize = GlobalSize(medium.unionmember);
                if (availableSize != 0 && requiredSize > checked((long)availableSize))
                {
                    throw new InvalidDataException("The clipboard file descriptor block is incomplete.");
                }

                var entries = new List<VirtualFileEntry>((int)count);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < count; index++)
                {
                    var itemPointer = IntPtr.Add(
                        descriptorPointer,
                        checked(4 + (int)index * descriptorSize));
                    var descriptor = Marshal.PtrToStructure<FILEDESCRIPTORW>(itemPointer);
                    if (!TryNormalizeRelativePath(descriptor.cFileName, out var relativePath))
                    {
                        throw new InvalidDataException(
                            $"The clipboard contains an unsafe file path: '{descriptor.cFileName}'.");
                    }

                    if (!paths.Add(relativePath))
                    {
                        throw new InvalidDataException(
                            $"The clipboard contains the duplicate path '{relativePath}'.");
                    }

                    var isDirectory = (descriptor.dwFlags & FD_ATTRIBUTES) != 0
                                      && ((FileAttributes)descriptor.dwFileAttributes
                                          & FileAttributes.Directory) != 0;
                    long? size = null;
                    if ((descriptor.dwFlags & FD_FILESIZE) != 0)
                    {
                        var unsignedSize = ((ulong)descriptor.nFileSizeHigh << 32)
                                           | descriptor.nFileSizeLow;
                        if (unsignedSize > long.MaxValue)
                        {
                            throw new InvalidDataException(
                                $"The clipboard reported an invalid size for '{relativePath}'.");
                        }

                        size = (long)unsignedSize;
                    }

                    var lastWriteTimeUtc = (descriptor.dwFlags & FD_WRITESTIME) != 0
                        ? TryConvertFileTime(descriptor.ftLastWriteTime)
                        : null;

                    entries.Add(new VirtualFileEntry(
                        (int)index,
                        relativePath,
                        isDirectory,
                        size,
                        lastWriteTimeUtc));
                }

                if (GetClipboardSequenceNumber() != clipboardSequenceNumber)
                {
                    throw new InvalidDataException("The clipboard changed while its files were being read.");
                }

                return new VirtualFileManifest(clipboardSequenceNumber, entries);
            }
            finally
            {
                GlobalUnlock(medium.unionmember);
            }
        }
        finally
        {
            if (hasMedium)
            {
                ReleaseStgMedium(ref medium);
            }
        }
    }

    private static VirtualFileMaterializationResult MaterializeVirtualFilesCore(
        IDataObject clipboard,
        VirtualFileManifest manifest,
        IProgress<PasteProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (GetClipboardSequenceNumber() != manifest.ClipboardSequenceNumber)
        {
            return VirtualFileMaterializationResult.Failed(
                manifest.FileCount,
                manifest.TotalBytes,
                "The clipboard changed before the paste started.");
        }

        var stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            "FileManager.NET",
            "Clipboard",
            Guid.NewGuid().ToString("N"));
        var errors = new List<string>();
        var filesCompleted = 0;
        long bytesCopied = 0;

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            ReportReceivingProgress(
                progress,
                null,
                filesCompleted,
                manifest.FileCount,
                bytesCopied,
                manifest.TotalBytes);

            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = GetStagingPath(stagingDirectory, entry.RelativePath);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                try
                {
                    var committedBytes = bytesCopied;
                    var fileBytes = CopyVirtualFile(
                        clipboard,
                        entry,
                        targetPath,
                        currentFileBytes => ReportReceivingProgress(
                            progress,
                            entry.RelativePath,
                            filesCompleted,
                            manifest.FileCount,
                            AddWithoutOverflow(committedBytes, currentFileBytes),
                            manifest.TotalBytes),
                        cancellationToken);

                    bytesCopied = AddWithoutOverflow(bytesCopied, fileBytes);
                    filesCompleted++;
                    ReportReceivingProgress(
                        progress,
                        entry.RelativePath,
                        filesCompleted,
                        manifest.FileCount,
                        bytesCopied,
                        manifest.TotalBytes);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
                {
                    TryDeleteFile(targetPath);
                    errors.Add($"{entry.RelativePath}: {ex.Message}");
                    Log.Warning(ex, "Failed to receive virtual clipboard file {Path}", entry.RelativePath);
                }
            }

            var sourcePaths = manifest.TopLevelNames
                .Select(name => Path.Combine(stagingDirectory, name))
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .ToArray();

            return new VirtualFileMaterializationResult(
                sourcePaths,
                stagingDirectory,
                filesCompleted,
                manifest.FileCount,
                bytesCopied,
                manifest.TotalBytes,
                false,
                errors);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    private static long CopyVirtualFile(
        IDataObject clipboard,
        VirtualFileEntry entry,
        string targetPath,
        Action<long> reportBytes,
        CancellationToken cancellationToken)
    {
        var medium = default(STGMEDIUM);
        var hasMedium = false;
        try
        {
            var format = CreateFormat(
                FileContentsFormatId.Value,
                entry.ContentIndex,
                TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL);
            clipboard.GetData(ref format, out medium);
            hasMedium = true;

            long copied;
            using (var destination = new FileStream(
                       targetPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.SequentialScan))
            {
                copied = medium.tymed switch
                {
                    TYMED.TYMED_ISTREAM => CopyStreamMedium(
                        medium.unionmember,
                        destination,
                        reportBytes,
                        cancellationToken),
                    TYMED.TYMED_HGLOBAL => CopyGlobalMedium(
                        medium.unionmember,
                        entry.Size,
                        destination,
                        reportBytes,
                        cancellationToken),
                    _ => throw new InvalidDataException(
                        $"Unsupported clipboard storage medium: {medium.tymed}."),
                };
            }

            if (entry.LastWriteTimeUtc is { } lastWriteTimeUtc)
            {
                File.SetLastWriteTimeUtc(targetPath, lastWriteTimeUtc);
            }

            return copied;
        }
        finally
        {
            if (hasMedium)
            {
                ReleaseStgMedium(ref medium);
            }
        }
    }

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
        Justification = "WindowsFileClipboard is only used by this Windows application.")]
    private static long CopyStreamMedium(
        IntPtr streamPointer,
        Stream destination,
        Action<long> reportBytes,
        CancellationToken cancellationToken)
    {
        if (streamPointer == IntPtr.Zero)
        {
            throw new InvalidDataException("The clipboard returned an empty file stream.");
        }

        var streamObject = Marshal.GetObjectForIUnknown(streamPointer);
        try
        {
            var source = (IStream)streamObject;
            var buffer = new byte[81920];
            var bytesReadPointer = Marshal.AllocHGlobal(sizeof(int));
            long copied = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Marshal.WriteInt32(bytesReadPointer, 0);
                    source.Read(buffer, buffer.Length, bytesReadPointer);
                    var bytesRead = Marshal.ReadInt32(bytesReadPointer);
                    if (bytesRead <= 0)
                    {
                        return copied;
                    }

                    destination.Write(buffer, 0, bytesRead);
                    copied = AddWithoutOverflow(copied, bytesRead);
                    reportBytes(copied);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(bytesReadPointer);
            }
        }
        finally
        {
            ReleaseComObject(streamObject);
        }
    }

    private static long CopyGlobalMedium(
        IntPtr globalHandle,
        long? declaredSize,
        Stream destination,
        Action<long> reportBytes,
        CancellationToken cancellationToken)
    {
        if (globalHandle == IntPtr.Zero)
        {
            throw new InvalidDataException("The clipboard returned an empty global memory block.");
        }

        var availableSize = GlobalSize(globalHandle);
        var size = declaredSize ?? checked((long)availableSize);
        if (size < 0 || (availableSize != 0 && size > checked((long)availableSize)))
        {
            throw new InvalidDataException("The clipboard returned an invalid file size.");
        }

        if (size == 0)
        {
            return 0;
        }

        var source = GlobalLock(globalHandle);
        if (source == IntPtr.Zero)
        {
            throw new IOException("The clipboard file contents could not be locked.");
        }

        try
        {
            var buffer = new byte[81920];
            long copied = 0;
            while (copied < size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, size - copied);
                Marshal.Copy(source + checked((nint)copied), buffer, 0, count);
                destination.Write(buffer, 0, count);
                copied += count;
                reportBytes(copied);
            }

            return copied;
        }
        finally
        {
            GlobalUnlock(globalHandle);
        }
    }

    private static FORMATETC CreateFormat(uint format, int index, TYMED medium) => new()
    {
        cfFormat = unchecked((short)format),
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = index,
        ptd = IntPtr.Zero,
        tymed = medium,
    };

    private static bool TryNormalizeRelativePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        var segments = path.Replace('/', '\\').Split('\\');
        if (segments.Any(segment => !IsSafePathSegment(segment)))
        {
            return false;
        }

        normalized = string.Join(Path.DirectorySeparatorChar, segments);
        return true;
    }

    private static bool IsSafePathSegment(string segment)
    {
        if (segment.Length == 0
            || segment is "." or ".."
            || segment.EndsWith(' ')
            || segment.EndsWith('.')
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var baseName = segment.Split('.', 2)[0].ToUpperInvariant();
        return baseName is not ("CON" or "PRN" or "AUX" or "NUL")
               && !(baseName.Length == 4
                    && (baseName.StartsWith("COM", StringComparison.Ordinal)
                        || baseName.StartsWith("LPT", StringComparison.Ordinal))
                    && baseName[3] is >= '1' and <= '9');
    }

    private static string GetStagingPath(string stagingDirectory, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(stagingDirectory, relativePath));
        var prefix = Path.EndsInDirectorySeparator(stagingDirectory)
            ? stagingDirectory
            : stagingDirectory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The clipboard path escapes the staging directory: '{relativePath}'.");
        }

        return path;
    }

    private static DateTime? TryConvertFileTime(FILETIME fileTime)
    {
        try
        {
            var value = ((long)(uint)fileTime.dwHighDateTime << 32)
                        | (uint)fileTime.dwLowDateTime;
            return value > 0 ? DateTime.FromFileTimeUtc(value) : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void ReportReceivingProgress(
        IProgress<PasteProgress>? progress,
        string? currentPath,
        int filesCompleted,
        int totalFiles,
        long bytesCopied,
        long totalBytes) =>
        progress?.Report(new PasteProgress(
            PasteProgressPhase.Receiving,
            currentPath,
            filesCompleted,
            totalFiles,
            bytesCopied,
            totalBytes));

    private static long AddWithoutOverflow(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to remove incomplete clipboard file {Path}", path);
        }
    }

    internal static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to remove clipboard staging directory {Directory}", path);
        }
    }

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
        Justification = "WindowsFileClipboard is only used by this Windows application.")]
    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    // Builds the double-null-terminated Unicode string list required by the DROPFILES structure.
    // Format: path\0path\0\0
    private static char[] BuildFileListChars(IReadOnlyList<string> paths)
    {
        int total = 1; // final null terminator
        foreach (var p in paths)
            total += p.Length + 1; // +1 for per-path null separator

        var chars = new char[total];
        int pos = 0;
        foreach (var p in paths)
        {
            p.AsSpan().CopyTo(chars.AsSpan(pos));
            pos += p.Length;
            chars[pos++] = '\0';
        }
        // chars[pos] is already '\0' (array default).
        return chars;
    }

    // Runs func on a dedicated STA thread (required by the Windows clipboard API)
    // and blocks until it completes.
    private static T RunOnSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }
}

internal sealed record VirtualFileEntry(
    int ContentIndex,
    string RelativePath,
    bool IsDirectory,
    long? Size,
    DateTime? LastWriteTimeUtc);

internal sealed record VirtualFileManifest(
    uint ClipboardSequenceNumber,
    IReadOnlyList<VirtualFileEntry> Entries)
{
    public int FileCount => Entries.Count(entry => !entry.IsDirectory);

    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var entry in Entries)
            {
                if (entry.IsDirectory || entry.Size is not { } size)
                {
                    continue;
                }

                total = total > long.MaxValue - size ? long.MaxValue : total + size;
            }

            return total;
        }
    }

    public IReadOnlyList<string> TopLevelNames =>
        Entries
            .Select(entry => entry.RelativePath.Split(Path.DirectorySeparatorChar, 2)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

internal sealed record VirtualFileMaterializationResult(
    IReadOnlyList<string> SourcePaths,
    string? StagingDirectory,
    int FilesReceived,
    int TotalFiles,
    long BytesReceived,
    long TotalBytes,
    bool Cancelled,
    IReadOnlyList<string> Errors)
{
    public static VirtualFileMaterializationResult CancelledResult(int totalFiles, long totalBytes) =>
        new([], null, 0, totalFiles, 0, totalBytes, true, []);

    public static VirtualFileMaterializationResult Failed(int totalFiles, long totalBytes, string error) =>
        new([], null, 0, totalFiles, 0, totalBytes, false, [error]);
}
