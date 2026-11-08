using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace McpBridge.Services;

/// <summary>
/// Opens the native Windows Explorer-style folder picker.
/// Unlike the legacy FolderBrowserDialog, IFileOpenDialog exposes the
/// address bar and supports direct path entry.
/// </summary>
internal static class NativeFolderPicker
{
    private const int HResultCancelled = unchecked((int)0x800704C7);

    [Flags]
    private enum FileOpenOptions : uint
    {
        PickFolders = 0x00000020,
        ForceFileSystem = 0x00000040,
        PathMustExist = 0x00000800,
        DontAddToRecent = 0x02000000
    }

    private enum ShellItemDisplayName : uint
    {
        FileSystemPath = 0x80058000
    }

    private static readonly Guid FileOpenDialogClassId =
        new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint count, IntPtr filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(ShellItemDisplayName nameType, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem item, uint hint, out int order);
    }

    public static string? Show(Window owner, string title)
    {
        IFileOpenDialog? dialog = null;
        IShellItem? selectedItem = null;
        IntPtr pathPointer = IntPtr.Zero;

        try
        {
            var dialogType = Type.GetTypeFromCLSID(FileOpenDialogClassId);
            if (dialogType == null)
                throw new InvalidOperationException("Windows folder picker COM class is unavailable.");

            dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;

            dialog.GetOptions(out var currentOptions);
            var options = currentOptions |
                (uint)(FileOpenOptions.PickFolders |
                       FileOpenOptions.ForceFileSystem |
                       FileOpenOptions.PathMustExist |
                       FileOpenOptions.DontAddToRecent);
            dialog.SetOptions(options);
            dialog.SetTitle(title);

            var ownerHandle = new WindowInteropHelper(owner).Handle;
            var result = dialog.Show(ownerHandle);

            if (result == HResultCancelled)
                return null;

            if (result < 0)
                Marshal.ThrowExceptionForHR(result);

            dialog.GetResult(out selectedItem);
            selectedItem.GetDisplayName(ShellItemDisplayName.FileSystemPath, out pathPointer);

            return Marshal.PtrToStringUni(pathPointer);
        }
        finally
        {
            if (pathPointer != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pathPointer);

            if (selectedItem != null && Marshal.IsComObject(selectedItem))
                Marshal.ReleaseComObject(selectedItem);

            if (dialog != null && Marshal.IsComObject(dialog))
                Marshal.ReleaseComObject(dialog);
        }
    }
}
