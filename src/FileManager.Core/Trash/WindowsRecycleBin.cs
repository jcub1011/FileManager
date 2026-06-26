using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using FileManager.Core.IO;

namespace FileManager.Core.Trash;

/// <summary>
/// Soft-deletes a file to the Windows Recycle Bin via the shell <c>IFileOperation</c> COM API (§5.3),
/// which is the supported way to populate the Recycle Bin (unlike a plain <c>File.Delete</c>).
/// </summary>
/// <remarks>
/// AOT/trim-safe by construction: the COM interfaces use source-generated marshalling
/// (<see cref="GeneratedComInterfaceAttribute"/>) rather than reflection-based <c>[ComImport]</c>
/// dispatch, and the object is created with a direct <c>CoCreateInstance</c> P/Invoke against an
/// explicit CLSID/IID instead of <c>Type.GetTypeFromProgID</c>/<c>Activator.CreateInstance</c>. There
/// is no late-bound dispatch, so the trim/AOT analyzers stay quiet. Guarded by
/// <see cref="OperatingSystem.IsWindows"/> at the call site (<see cref="TrashServiceFactory"/>).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsRecycleBin(IFileOperations files) : ITrashService
{
    // FOF_* / FOFX_* operation flags (shellapi.h): silent, no UI, allow undo (Recycle Bin),
    // no confirmation, no error UI, suppress all dialogs.
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOFX_EARLYFAILURE = 0x00100000;

    private const uint OperationFlags =
        FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI | FOFX_EARLYFAILURE;

    // CLSID_FileOperation and IID_IShellItem.
    private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    private const int CLSCTX_INPROC_SERVER = 0x1;

    public TrashResult MoveToTrash(string path)
    {
        if (!files.FileExists(path))
            return TrashResult.Failure($"File not found: {path}");

        string full = Path.GetFullPath(path);
        object? comObject = null;
        try
        {
            int hr = CoCreateInstance(
                in CLSID_FileOperation, IntPtr.Zero, CLSCTX_INPROC_SERVER, in IID_IFileOperation, out comObject);
            if (hr < 0)
                return TrashResult.Failure($"CoCreateInstance(FileOperation) failed (0x{hr:X8}).");

            var op = (IFileOperation)comObject;
            op.SetOperationFlags(OperationFlags);

            int siHr = SHCreateItemFromParsingName(full, IntPtr.Zero, in IID_IShellItem, out object itemObj);
            if (siHr < 0)
                return TrashResult.Failure($"SHCreateItemFromParsingName failed (0x{siHr:X8}).");

            var item = (IShellItem)itemObj;
            op.DeleteItem(item, null);
            op.PerformOperations();

            // PerformOperations can return success even when the shell silently aborted the delete
            // (e.g. the file exceeds the Recycle Bin's per-drive quota, or policy forbids the Bin) — in
            // which case the file was NOT trashed. MS docs require checking GetAnyOperationsAborted
            // regardless of the PerformOperations result; treat an abort as a failure so the caller
            // never assumes a non-recoverable source was safely trashed (§3.1.1 "never hard-deleted").
            if (op.GetAnyOperationsAborted())
                return TrashResult.Failure("IFileOperation aborted the delete (Recycle Bin may be full or restricted).");

            // IFileOperation does not surface the per-item Recycle Bin path; null is acceptable
            // (TrashResult.TrashedPath is best-effort for the audit trail).
            return TrashResult.Success(null);
        }
        catch (COMException ex)
        {
            return TrashResult.Failure($"IFileOperation failed: 0x{ex.HResult:X8} {ex.Message}");
        }
        finally
        {
            // Source-generated COM wrappers (ComObject) are IDisposable; disposing releases the
            // underlying RCW. This is the AOT-safe equivalent of Marshal.FinalReleaseComObject, which
            // only supports the legacy reflection-based interop.
            (comObject as IDisposable)?.Dispose();
        }
    }

    private static readonly Guid IID_IFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        int dwClsContext,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
}

/// <summary>Minimal <c>IShellItem</c> surface — declared so <c>IFileOperation.DeleteItem</c> can take it.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal partial interface IShellItem
{
    void BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);

    void GetParent(out IShellItem ppsi);

    void GetDisplayName(uint sigdnName, out IntPtr ppszName);

    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    void Compare(IShellItem psi, uint hint, out int piOrder);
}

/// <summary>
/// Minimal <c>IFileOperationProgressSink</c> surface (unused — <c>DeleteItem</c> accepts a null sink),
/// declared only to type the parameter.
/// </summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal partial interface IFileOperationProgressSink
{
    void StartOperations();
    void FinishOperations(int hrResult);
    void PreRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName);
    void PostRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName, int hrRename, IShellItem psiNewlyCreated);
    void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName);
    void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrMove, IShellItem psiNewlyCreated);
    void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName);
    void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrCopy, IShellItem psiNewlyCreated);
    void PreDeleteItem(uint dwFlags, IShellItem psiItem);
    void PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem psiNewlyCreated);
    void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName);
    void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName, string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem psiNewItem);
    void UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
    void ResetTimer();
    void PauseTimer();
    void ResumeTimer();
}

/// <summary>
/// The subset of shell <c>IFileOperation</c> the Recycle Bin path uses (set flags, queue a delete,
/// run). Declared with source-generated COM marshalling for AOT safety.
/// </summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal partial interface IFileOperation
{
    uint Advise(IFileOperationProgressSink pfops);
    void Unadvise(uint dwCookie);
    void SetOperationFlags(uint dwOperationFlags);
    void SetProgressMessage(string pszMessage);
    void SetProgressDialog(IntPtr popd);
    void SetProperties(IntPtr pproparray);
    void SetOwnerWindow(uint hwndOwner);
    void ApplyPropertiesToItem(IShellItem psiItem);
    void ApplyPropertiesToItems(IntPtr punkItems);
    void RenameItem(IShellItem psiItem, string pszNewName, IFileOperationProgressSink? pfopsItem);
    void RenameItems(IntPtr pUnkItems, string pszNewName);
    void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, IFileOperationProgressSink? pfopsItem);
    void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
    void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, string? pszCopyName, IFileOperationProgressSink? pfopsItem);
    void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
    void DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);
    void DeleteItems(IntPtr punkItems);
    void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, string pszName, string pszTemplateName, IFileOperationProgressSink? pfopsItem);
    void PerformOperations();
    [return: MarshalAs(UnmanagedType.Bool)]
    bool GetAnyOperationsAborted();
}
