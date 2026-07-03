using System.Runtime.InteropServices;

namespace FileManager.NET.Platform;

/// <summary>
/// Provides access to the Windows clipboard for file-system items using the CF_HDROP format
/// (the same format Explorer uses for file copy/paste). All operations are dispatched on a
/// dedicated STA thread as required by the Windows clipboard API.
/// </summary>
internal static class WindowsFileClipboard
{
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct DROPFILES
    {
        public uint pFiles;  // Byte offset of the file list from the start of this structure.
        public int pt_x;     // Unused drop point; kept zero.
        public int pt_y;
        public int fNC;      // 0 = client-area drop.
        public int fWide;    // 1 = file names are Unicode strings.
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, char[]? lpszFile, uint cch);

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
        var thread = new Thread(() => result = func());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }
}
