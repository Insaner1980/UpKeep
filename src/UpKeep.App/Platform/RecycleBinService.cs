using System.Runtime.InteropServices;
using UpKeep.Core.Cleanup;
using UpKeep.Core.Models;

namespace UpKeep_App.Platform;

public sealed partial class RecycleBinService : IRecycleBinService
{
    public Task<IReadOnlyList<CleanupItemResult>> RecycleAsync(IReadOnlyList<ValidatedCleanupFile> files, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<CleanupItemResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var results = new List<CleanupItemResult>();
            try
            {
                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        results.Add(new(file.Candidate.Id, "Canceled", 0, "Canceled before recycling."));
                        continue;
                    }
                    RecycleFile(file, results, cancellationToken);
                }
                completion.SetResult(results);
            }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
    private static void RecycleFile(ValidatedCleanupFile file, List<CleanupItemResult> results, CancellationToken cancellationToken)
    {
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"))!)!;
            // Require recycling, decline errors, never answer Yes to a permanent-delete fallback.
            operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00100000 | 0x0400 | 0x4000 | 0x2000 | 0x1000);
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(file.Path, 0, ref iid, out var pointer));
            try
            {
                item = (IShellItem)Marshal.GetObjectForIUnknown(pointer);
            }
            finally { Marshal.Release(pointer); }
            var sink = new Sink(file, cancellationToken);
            operation.DeleteItem(item, sink);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            results.Add(sink.Recycled && !aborted ? new(file.Candidate.Id, "Recycled", file.Candidate.LogicalSize, "Moved to Recycle Bin; disk space has not been reclaimed.") :
                new(file.Candidate.Id, "Skipped", 0, "Recycling was unavailable, declined, or canceled. No permanent-delete fallback was requested."));
        }
        catch (COMException error) { results.Add(new(file.Candidate.Id, "Skipped", 0, "Recycling failed: 0x" + error.HResult.ToString("X8"))); }
        finally
        {
            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }

            if (operation is not null)
            {
                Marshal.ReleaseComObject(operation);
            }
        }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Sink(ValidatedCleanupFile file, CancellationToken token) : IFileOperationProgressSink
    {
        public bool Recycled
        {
            get; private set;
        }
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            try
            {
                return !token.IsCancellationRequested && (flags & 0x80) != 0 && PathGuard.Snapshot(file.Path) == file.Candidate.Identity ? 0 : unchecked((int)0x80004004);
            }
            catch { return unchecked((int)0x80004004); }
        }
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
        {
            Recycled = result >= 0 && created is not null;
            return 0;
        }
        public int StartOperations() => 0; public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string name) => 0; public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string name) => 0; public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string name) => 0; public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? created) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string name) => 0; public int PostNewItem(uint flags, IShellItem destination, string name, string template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint done) => 0; public int ResetTimer() => 0; public int PauseTimer() => 0; public int ResumeTimer() => 0;
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1096", Justification = "Uses the runtime COM identity shared with the Shell progress-sink CCW and explicit ReleaseComObject lifetime; source-generated COM wrappers cannot be mixed with that lifetime model.")]
    private interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid iid, out nint result); void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, out nint name); void GetAttributes(uint mask, out uint attributes); void Compare(IShellItem other, uint hint, out int order);
    }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1096", Justification = "Uses a runtime-marshalled Shell progress-sink CCW and explicit ReleaseComObject lifetime; this unpackaged JIT application does not use NativeAOT.")]
    private interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie); void Unadvise(uint cookie); void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message); void SetProgressDialog(nint dialog); void SetProperties(nint properties); void SetOwnerWindow(nint hwnd);
        void ApplyPropertiesToItem(IShellItem item); void ApplyPropertiesToItems(nint items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink); void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink); void MoveItems(nint items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink); void CopyItems(nint items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink); void DeleteItems(nint items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink? sink);
        void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations(); [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name); [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name); [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name); [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item); [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name); [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint done); [PreserveSig] int ResetTimer(); [PreserveSig] int PauseTimer(); [PreserveSig] int ResumeTimer();
    }
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int SHCreateItemFromParsingName(string path, nint bind, ref Guid iid, out nint item);
}
