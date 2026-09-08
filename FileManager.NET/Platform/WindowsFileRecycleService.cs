using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Serilog;
using FileManager.NET.Core.FileSystem;

#pragma warning disable CA1416 // This service is only composed by the Windows application.

namespace FileManager.NET.Platform;

internal sealed class WindowsFileRecycleService : IRecycleService
{
    private readonly BlockingCollection<Action> _workItems = [];
    private readonly Dictionary<Guid, RecycledHandle> _recycledItems = [];
    private readonly Thread _worker;
    private int _disposed;

    public WindowsFileRecycleService()
    {
        _worker = new Thread(RunWorker)
        {
            IsBackground = true,
            Name = "FileManager.NET Shell file operations",
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public Task<RecycleResult> RecycleAsync(IReadOnlyList<FileSystemEntry> entries)
    {
        var items = entries
            .Select(entry => new RecycleRequest(
                entry.FullPath,
                entry.IsDirectory))
            .ToArray();
        return Enqueue(() => RecycleCore(items));
    }

    public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItemToken> items) =>
        Enqueue(() => RestoreCore(items));

    public void Release(IReadOnlyList<RecycledItemToken> items)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _workItems.Add(() =>
            {
                foreach (var item in items)
                {
                    ReleaseHandle(item.Id);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _workItems.CompleteAdding();
        _worker.Join();
        _workItems.Dispose();
    }

    private Task<T> Enqueue<T>(Func<T> operation)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.FromException<T>(
                new ObjectDisposedException(nameof(WindowsFileRecycleService)));
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _workItems.Add(() =>
            {
                try
                {
                    completion.SetResult(operation());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            completion.SetException(
                new ObjectDisposedException(nameof(WindowsFileRecycleService)));
        }

        return completion.Task;
    }

    private void RunWorker()
    {
        try
        {
            foreach (var workItem in _workItems.GetConsumingEnumerable())
            {
                workItem();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Windows Shell file-operation worker failed");
        }
        finally
        {
            foreach (var handle in _recycledItems.Values)
            {
                if (handle.ShellItem != IntPtr.Zero)
                {
                    Marshal.Release(handle.ShellItem);
                }
            }

            _recycledItems.Clear();
        }
    }

    private RecycleResult RecycleCore(IReadOnlyList<RecycleRequest> requests)
    {
        var failures = new List<RecycleFailure>();
        var pending = new List<PendingRecycle>();
        IFileOperation? operation = null;
        Exception? operationFailure = null;

        try
        {
            operation = CreateOperation();
            operation.SetOperationFlags(
                FileOperationFlags.Silent
                | FileOperationFlags.NoConfirmation
                | FileOperationFlags.AllowUndo
                | FileOperationFlags.NoErrorUi
                | FileOperationFlags.RecycleOnDelete
                | FileOperationFlags.EarlyFailure);

            foreach (var request in requests)
            {
                if (!File.Exists(request.Path) && !Directory.Exists(request.Path))
                {
                    failures.Add(new RecycleFailure(request.Path, "The item no longer exists."));
                    continue;
                }

                IShellItem? sourceItem = null;
                IntPtr sinkPointer = IntPtr.Zero;
                try
                {
                    sourceItem = CreateShellItem(request.Path);
                    var sink = new FileOperationProgressSink();
                    sinkPointer = Marshal.GetComInterfaceForObject(
                        sink,
                        typeof(IFileOperationProgressSink));
                    operation.DeleteItem(sourceItem, sinkPointer);
                    pending.Add(new PendingRecycle(
                        request,
                        sourceItem,
                        sink,
                        sinkPointer));
                    sourceItem = null;
                    sinkPointer = IntPtr.Zero;
                }
                catch (Exception ex) when (IsExpectedShellException(ex))
                {
                    failures.Add(new RecycleFailure(request.Path, ex.Message));
                    Log.Warning(ex, "Failed to queue {Path} for Recycle Bin deletion", request.Path);
                }
                finally
                {
                    ReleaseComObject(sourceItem);
                    if (sinkPointer != IntPtr.Zero)
                    {
                        Marshal.Release(sinkPointer);
                    }
                }
            }

            if (pending.Count > 0)
            {
                try
                {
                    operation.PerformOperations();
                }
                catch (Exception ex) when (IsExpectedShellException(ex))
                {
                    operationFailure = ex;
                    Log.Warning(ex, "Windows Shell Recycle Bin operation failed");
                }
                catch (Exception ex)
                {
                    operationFailure = ex;
                    Log.Error(ex, "Unexpected Windows Shell Recycle Bin failure");
                }
            }
        }
        catch (Exception ex) when (IsExpectedShellException(ex))
        {
            operationFailure = ex;
            Log.Warning(ex, "Failed to initialize Windows Shell Recycle Bin operation");
        }
        catch (Exception ex)
        {
            operationFailure = ex;
            Log.Error(ex, "Unexpected failure initializing Windows Shell Recycle Bin operation");
        }
        finally
        {
            ReleaseComObject(operation);
        }

        var recycled = new List<RecycledItemToken>();
        var unrecoverable = new List<string>();
        foreach (var item in pending)
        {
            try
            {
                var recycledShellItem = item.Sink.TakeRecycledItem();
                if (item.Sink.DeleteResult is int result && result >= 0 && recycledShellItem != IntPtr.Zero)
                {
                    var token = new RecycledItemToken(
                        Guid.NewGuid(),
                        item.Request.Path,
                        item.Request.IsDirectory);
                    _recycledItems.Add(
                        token.Id,
                        new RecycledHandle(token, recycledShellItem));
                    recycled.Add(token);
                    recycledShellItem = IntPtr.Zero;
                    continue;
                }

                if (recycledShellItem != IntPtr.Zero)
                {
                    Marshal.Release(recycledShellItem);
                }
                if (!File.Exists(item.Request.Path) && !Directory.Exists(item.Request.Path))
                {
                    unrecoverable.Add(item.Request.Path);
                    Log.Error(
                        "Windows Shell deleted {Path} without returning a recoverable Recycle Bin item",
                        item.Request.Path);
                    continue;
                }

                var error = item.Sink.DeleteResult is int hresult && hresult < 0
                    ? GetShellError(hresult)
                    : operationFailure?.Message ?? "Windows did not recycle the item.";
                failures.Add(new RecycleFailure(item.Request.Path, error));
            }
            finally
            {
                ReleaseComObject(item.SourceItem);
                Marshal.Release(item.SinkPointer);
            }
        }

        var accountedPaths = new HashSet<string>(
            failures.Select(failure => failure.Path),
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in pending)
        {
            accountedPaths.Add(item.Request.Path);
        }

        foreach (var request in requests)
        {
            if (accountedPaths.Contains(request.Path))
            {
                continue;
            }

            failures.Add(new RecycleFailure(
                request.Path,
                operationFailure?.Message ?? "Windows could not start the Recycle Bin operation."));
        }

        return new RecycleResult(recycled, failures, unrecoverable);
    }

    private RestoreResult RestoreCore(IReadOnlyList<RecycledItemToken> items)
    {
        var failures = new List<RestoreFailure>();
        var pending = new List<PendingRestore>();
        var restored = new List<RecycledItemToken>();
        var changedPaths = new List<string>();
        IFileOperation? operation = null;
        Exception? operationFailure = null;

        try
        {
            operation = CreateOperation();
            operation.SetOperationFlags(
                FileOperationFlags.Silent
                | FileOperationFlags.RenameOnCollision
                | FileOperationFlags.NoConfirmation
                | FileOperationFlags.NoErrorUi
                | FileOperationFlags.EarlyFailure);

            foreach (var item in items)
            {
                if (!_recycledItems.TryGetValue(item.Id, out var handle))
                {
                    failures.Add(new RestoreFailure(
                        item,
                        "The Recycle Bin item is no longer available.",
                        Retryable: false));
                    continue;
                }

                if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
                {
                    failures.Add(new RestoreFailure(
                        item,
                        "The original path is already occupied.",
                        Retryable: true));
                    continue;
                }

                var parentPath = Path.GetDirectoryName(item.OriginalPath);
                if (parentPath is null || !Directory.Exists(parentPath))
                {
                    failures.Add(new RestoreFailure(
                        item,
                        "The original parent directory is unavailable.",
                        Retryable: true));
                    continue;
                }

                IShellItem? destination = null;
                IntPtr sinkPointer = IntPtr.Zero;
                try
                {
                    destination = CreateShellItem(parentPath);
                    var sink = new FileOperationProgressSink();
                    sinkPointer = Marshal.GetComInterfaceForObject(
                        sink,
                        typeof(IFileOperationProgressSink));
                    operation.MoveItem(
                        handle.ShellItem,
                        destination,
                        Path.GetFileName(item.OriginalPath),
                        sinkPointer);
                    pending.Add(new PendingRestore(
                        item,
                        destination,
                        sink,
                        sinkPointer));
                    destination = null;
                    sinkPointer = IntPtr.Zero;
                }
                catch (COMException ex)
                {
                    var retryable = !IsMissingItemError(ex.ErrorCode);
                    failures.Add(new RestoreFailure(item, ex.Message, retryable));
                    if (!retryable)
                    {
                        ReleaseHandle(item.Id);
                    }

                    Log.Warning(ex, "Failed to queue restore of {Path}", item.OriginalPath);
                }
                catch (Exception ex) when (IsExpectedShellException(ex))
                {
                    failures.Add(new RestoreFailure(item, ex.Message, Retryable: true));
                    Log.Warning(ex, "Failed to queue restore of {Path}", item.OriginalPath);
                }
                finally
                {
                    ReleaseComObject(destination);
                    if (sinkPointer != IntPtr.Zero)
                    {
                        Marshal.Release(sinkPointer);
                    }
                }
            }

            if (pending.Count > 0)
            {
                try
                {
                    operation.PerformOperations();
                }
                catch (Exception ex) when (IsExpectedShellException(ex))
                {
                    operationFailure = ex;
                    Log.Warning(ex, "Windows Shell restore operation failed");
                }
                catch (Exception ex)
                {
                    operationFailure = ex;
                    Log.Error(ex, "Unexpected Windows Shell restore failure");
                }
            }
        }
        catch (Exception ex) when (IsExpectedShellException(ex))
        {
            operationFailure = ex;
            Log.Warning(ex, "Failed to initialize Windows Shell restore operation");
        }
        catch (Exception ex)
        {
            operationFailure = ex;
            Log.Error(ex, "Unexpected failure initializing Windows Shell restore operation");
        }
        finally
        {
            ReleaseComObject(operation);
        }

        foreach (var item in pending)
        {
            try
            {
                var movedItem = item.Sink.TakeMovedItem();
                if (item.Sink.MoveResult is int result && result >= 0)
                {
                    var restoredPath = movedItem == IntPtr.Zero
                        ? null
                        : GetShellItemPath(movedItem);
                    if (string.Equals(
                            restoredPath,
                            item.Item.OriginalPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        restored.Add(item.Item);
                        changedPaths.Add(item.Item.OriginalPath);
                        ReleaseHandle(item.Item.Id);
                    }
                    else if (restoredPath is not null
                             && _recycledItems.TryGetValue(item.Item.Id, out var handle))
                    {
                        changedPaths.Add(restoredPath);
                        failures.Add(new RestoreFailure(
                            item.Item,
                            $"The original path became occupied; the item was restored as '{restoredPath}'.",
                            Retryable: false));
                        ReleaseHandle(handle.Token.Id);
                    }
                    else
                    {
                        failures.Add(new RestoreFailure(
                            item.Item,
                            "Windows reported success but did not return the restored path.",
                            Retryable: true));
                    }

                    if (movedItem != IntPtr.Zero)
                    {
                        Marshal.Release(movedItem);
                    }

                    continue;
                }

                if (movedItem != IntPtr.Zero)
                {
                    Marshal.Release(movedItem);
                }

                var error = item.Sink.MoveResult is int hresult && hresult < 0
                    ? GetShellError(hresult)
                    : operationFailure?.Message ?? "Windows did not restore the item.";
                var retryable = item.Sink.MoveResult is not int moveResult
                                || !IsMissingItemError(moveResult);
                failures.Add(new RestoreFailure(item.Item, error, retryable));
                if (!retryable)
                {
                    ReleaseHandle(item.Item.Id);
                }
            }
            finally
            {
                ReleaseComObject(item.Destination);
                Marshal.Release(item.SinkPointer);
            }
        }

        var accountedIds = new HashSet<Guid>(
            failures.Select(failure => failure.Item.Id)
                .Concat(restored.Select(item => item.Id)));
        foreach (var item in pending)
        {
            accountedIds.Add(item.Item.Id);
        }

        foreach (var item in items)
        {
            if (!accountedIds.Contains(item.Id))
            {
                failures.Add(new RestoreFailure(
                    item,
                    operationFailure?.Message ?? "Windows could not start the restore operation.",
                    Retryable: true));
            }
        }

        return new RestoreResult(restored, failures, changedPaths);
    }

    private static IFileOperation CreateOperation() =>
        (IFileOperation)(object)new FileOperationComObject();

    private static IShellItem CreateShellItem(string path)
    {
        var interfaceId = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName(
            path,
            IntPtr.Zero,
            ref interfaceId,
            out var item);
        return item;
    }

    private void ReleaseHandle(Guid id)
    {
        if (_recycledItems.Remove(id, out var handle))
        {
            if (handle.ShellItem != IntPtr.Zero)
            {
                Marshal.Release(handle.ShellItem);
            }
        }
    }

    private static string? GetShellItemPath(IntPtr shellItemPointer)
    {
        object? shellObject = null;
        IntPtr name = IntPtr.Zero;
        try
        {
            shellObject = Marshal.GetObjectForIUnknown(shellItemPointer);
            ((IShellItem)shellObject).GetDisplayName(
                0x80058000,
                out name);
            return Marshal.PtrToStringUni(name);
        }
        catch (Exception ex) when (IsExpectedShellException(ex))
        {
            Log.Warning(ex, "Failed to read restored Shell item path");
            return null;
        }
        finally
        {
            if (name != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(name);
            }

            ReleaseComObject(shellObject);
        }
    }

    private static string GetShellError(int hresult) =>
        Marshal.GetExceptionForHR(hresult)?.Message
        ?? $"Windows Shell error 0x{hresult:X8}.";

    private static bool IsMissingItemError(int hresult) =>
        hresult is unchecked((int)0x80070002)
            or unchecked((int)0x80070003);

    private static bool IsExpectedShellException(Exception ex) =>
        ex is COMException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [Flags]
    private enum FileOperationFlags : uint
    {
        Silent = 0x0004,
        RenameOnCollision = 0x0008,
        NoConfirmation = 0x0010,
        AllowUndo = 0x0040,
        NoErrorUi = 0x0400,
        RecycleOnDelete = 0x00080000,
        EarlyFailure = 0x00100000,
    }

    [ComImport]
    [Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FileOperationComObject;

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr progressSink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        void SetOperationFlags(FileOperationFlags operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object progressDialog);
        [PreserveSig] int SetProperties([MarshalAs(UnmanagedType.Interface)] object properties);
        [PreserveSig] int SetOwnerWindow(IntPtr ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int RenameItem(
            IShellItem item,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);
        [PreserveSig] int RenameItems(
            [MarshalAs(UnmanagedType.IUnknown)] object items,
            [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(
            IntPtr item,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);
        [PreserveSig] int MoveItems(
            [MarshalAs(UnmanagedType.IUnknown)] object items,
            IShellItem destinationFolder);
        [PreserveSig] int CopyItem(
            IShellItem item,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string copyName,
            IntPtr progressSink);
        [PreserveSig] int CopyItems(
            [MarshalAs(UnmanagedType.IUnknown)] object items,
            IShellItem destinationFolder);
        void DeleteItem(IShellItem item, IntPtr progressSink);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int NewItem(
            IShellItem destinationFolder,
            FileAttributes attributes,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string templateName,
            IntPtr progressSink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(
            IntPtr bindContext,
            ref Guid handlerId,
            ref Guid interfaceId,
            out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint displayNameType, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FileOperationProgressSink : IFileOperationProgressSink
    {
        public int? DeleteResult { get; private set; }
        public int? MoveResult { get; private set; }
        private IntPtr RecycledItem { get; set; }
        private IntPtr MovedItem { get; set; }

        public IntPtr TakeRecycledItem()
        {
            var item = RecycledItem;
            RecycledItem = IntPtr.Zero;
            return item;
        }

        public IntPtr TakeMovedItem()
        {
            var item = MovedItem;
            MovedItem = IntPtr.Zero;
            return item;
        }

        public void StartOperations() { }
        public void FinishOperations(int result) { }
        public void PreRenameItem(uint flags, IntPtr item, string newName) { }
        public void PostRenameItem(uint flags, IntPtr item, string newName, int result, IntPtr newItem) { }
        public void PreMoveItem(uint flags, IntPtr item, IntPtr destinationFolder, string newName) { }

        public void PostMoveItem(
            uint flags,
            IntPtr item,
            IntPtr destinationFolder,
            string newName,
            int result,
            IntPtr newItem)
        {
            MoveResult = result;
            if (newItem != IntPtr.Zero)
            {
                Marshal.AddRef(newItem);
                MovedItem = newItem;
            }
        }

        public void PreCopyItem(uint flags, IntPtr item, IntPtr destinationFolder, string newName) { }
        public void PostCopyItem(
            uint flags,
            IntPtr item,
            IntPtr destinationFolder,
            string newName,
            int result,
            IntPtr newItem) { }

        public int PreDeleteItem(uint flags, IntPtr item) =>
            (flags & 0x80) != 0
                ? 0
                : unchecked((int)0x80004005);

        public void PostDeleteItem(
            uint flags,
            IntPtr item,
            int result,
            IntPtr recycledItem)
        {
            DeleteResult = result;
            if (recycledItem != IntPtr.Zero)
            {
                Marshal.AddRef(recycledItem);
                RecycledItem = recycledItem;
            }
        }

        public void PreNewItem(uint flags, IntPtr destinationFolder, string newName) { }
        public void PostNewItem(
            uint flags,
            IntPtr destinationFolder,
            string newName,
            string templateName,
            FileAttributes attributes,
            int result,
            IntPtr newItem) { }
        public void UpdateProgress(uint workTotal, uint workSoFar) { }
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }

    private sealed record RecycleRequest(string Path, bool IsDirectory);

    private sealed record PendingRecycle(
        RecycleRequest Request,
        IShellItem SourceItem,
        FileOperationProgressSink Sink,
        IntPtr SinkPointer);

    private sealed record PendingRestore(
        RecycledItemToken Item,
        IShellItem Destination,
        FileOperationProgressSink Sink,
        IntPtr SinkPointer);

    private sealed record RecycledHandle(
        RecycledItemToken Token,
        IntPtr ShellItem);
}

[ComVisible(true)]
[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
// Public visibility is required for Marshal.GetComInterfaceForObject to expose the callback.
public interface IFileOperationProgressSink
{
    void StartOperations();
    void FinishOperations(int result);
    void PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string newName);
    void PostRenameItem(
        uint flags,
        IntPtr item,
        [MarshalAs(UnmanagedType.LPWStr)] string newName,
        int result,
        IntPtr newItem);
    void PreMoveItem(
        uint flags,
        IntPtr item,
        IntPtr destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string newName);
    void PostMoveItem(
        uint flags,
        IntPtr item,
        IntPtr destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string newName,
        int result,
        IntPtr newItem);
    void PreCopyItem(
        uint flags,
        IntPtr item,
        IntPtr destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string newName);
    void PostCopyItem(
        uint flags,
        IntPtr item,
        IntPtr destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string newName,
        int result,
        IntPtr newItem);
    [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
    void PostDeleteItem(uint flags, IntPtr item, int result, IntPtr recycledItem);
    void PreNewItem(
        uint flags,
        IntPtr destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string newName);
    void PostNewItem(
        uint flags,
        IntPtr destinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string newName,
        [MarshalAs(UnmanagedType.LPWStr)] string templateName,
        FileAttributes attributes,
        int result,
        IntPtr newItem);
    void UpdateProgress(uint workTotal, uint workSoFar);
    void ResetTimer();
    void PauseTimer();
    void ResumeTimer();
}
