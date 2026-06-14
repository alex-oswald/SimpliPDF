using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace SimpliPDF.Helpers;

/// <summary>
/// Win32 file dialogs via CsWin32 COM — reliable in unpackaged WinUI 3 apps.
/// </summary>
public static class Win32FileDialog
{
    public static string[]? OpenPdfFiles(IntPtr hwnd)
    {
        IFileOpenDialog? dialog = CreateDialog<Windows.Win32.UI.Shell.IFileOpenDialog>(
            "DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        if (dialog == null) return null;

        try
        {
            dialog.GetOptions(out FILEOPENDIALOGOPTIONS options);
            dialog.SetOptions(options
                | Windows.Win32.UI.Shell.FILEOPENDIALOGOPTIONS.FOS_ALLOWMULTISELECT
                | Windows.Win32.UI.Shell.FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST);
            dialog.SetTitle("Open PDF Files");
            SetPdfFilter(dialog);

            try { dialog.Show(new HWND(hwnd)); }
            catch { return null; }

            dialog.GetResults(out IShellItemArray? items);
            items.GetCount(out uint count);
            List<string> paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out IShellItem? item);
                item.GetDisplayName(Windows.Win32.UI.Shell.SIGDN.SIGDN_FILESYSPATH, out PWSTR path);
                paths.Add(path.ToString());
            }
            return paths.Count > 0 ? paths.ToArray() : null;
        }
        catch { return null; }
    }

    public static string? SavePdfFile(IntPtr hwnd, string defaultName = "Merged.pdf")
    {
        IFileSaveDialog? dialog = CreateDialog<Windows.Win32.UI.Shell.IFileSaveDialog>(
            "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
        if (dialog == null) return null;

        try
        {
            SetPdfFilter(dialog);
            dialog.SetDefaultExtension("pdf");
            dialog.SetFileName(defaultName);

            try { dialog.Show(new HWND(hwnd)); }
            catch { return null; }

            dialog.GetResult(out IShellItem? item);
            item.GetDisplayName(Windows.Win32.UI.Shell.SIGDN.SIGDN_FILESYSPATH, out PWSTR path);
            return path.ToString();
        }
        catch { return null; }
    }

    public static string? PickFolder(IntPtr hwnd)
    {
        IFileOpenDialog? dialog = CreateDialog<Windows.Win32.UI.Shell.IFileOpenDialog>(
            "DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        if (dialog == null) return null;

        try
        {
            dialog.GetOptions(out FILEOPENDIALOGOPTIONS options);
            dialog.SetOptions(options | Windows.Win32.UI.Shell.FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS);
            dialog.SetTitle("Select output folder");

            try { dialog.Show(new HWND(hwnd)); }
            catch { return null; }

            dialog.GetResult(out IShellItem? item);
            item.GetDisplayName(Windows.Win32.UI.Shell.SIGDN.SIGDN_FILESYSPATH, out PWSTR path);
            return path.ToString();
        }
        catch { return null; }
    }

    private static T? CreateDialog<T>(string clsidStr) where T : class
    {
        Guid clsid = new Guid(clsidStr);
        Guid iid = typeof(T).GUID;
        PInvoke.CoCreateInstance(in clsid, null, Windows.Win32.System.Com.CLSCTX.CLSCTX_INPROC_SERVER, in iid, out object? ppv);
        return ppv as T;
    }

    /// <summary>
    /// Set PDF file filter on a file dialog using raw COM vtable call
    /// to avoid the unsafe COMDLG_FILTERSPEC pointer requirement.
    /// </summary>
    private static void SetPdfFilter(object dialog)
    {
        // IFileDialog::SetFileTypes is vtable slot index 4 (after IUnknown 0-2 and IModalWindow::Show at 3)
        // But the COM interface is wrapped by CsWin32 / .NET RCW, so we use reflection on the interface.
        // Alternatively, just set the default extension — the dialog will filter naturally.
        // For a clean UX without unsafe, we rely on SetDefaultExtension + SetFileName.
        // The dialog will show "*.pdf" as the active filter via the extension.
        try
        {
            // Use dynamic to call SetFileTypes(uint, ptr) with marshaled struct
            nint pdfName = Marshal.StringToCoTaskMemUni("PDF Files");
            nint pdfSpec = Marshal.StringToCoTaskMemUni("*.pdf");
            nint allName = Marshal.StringToCoTaskMemUni("All Files");
            nint allSpec = Marshal.StringToCoTaskMemUni("*.*");

            // COMDLG_FILTERSPEC is { LPCWSTR pszName; LPCWSTR pszSpec; } = two IntPtrs
            int structSize = IntPtr.Size * 2;
            nint filterMem = Marshal.AllocCoTaskMem(structSize * 2);

            Marshal.WriteIntPtr(filterMem, 0, pdfName);
            Marshal.WriteIntPtr(filterMem, IntPtr.Size, pdfSpec);
            Marshal.WriteIntPtr(filterMem, structSize, allName);
            Marshal.WriteIntPtr(filterMem, structSize + IntPtr.Size, allSpec);

            // Get the COM interface pointer and call SetFileTypes via vtable
            nint punk = Marshal.GetIUnknownForObject(dialog);
            Guid iidFileDialog = new Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"); // IID_IFileDialog
            Marshal.QueryInterface(punk, in iidFileDialog, out nint pFileDialog);
            Marshal.Release(punk);

            // IFileDialog vtable: IUnknown(3) + Show(1) + SetFileTypes is index 4
            nint vtable = Marshal.ReadIntPtr(pFileDialog);
            nint setFileTypesPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 4);

            // delegate: HRESULT SetFileTypes(IFileDialog*, uint cFileTypes, COMDLG_FILTERSPEC* rgFilterSpec)
            SetFileTypesDelegate setFileTypes = Marshal.GetDelegateForFunctionPointer<SetFileTypesDelegate>(setFileTypesPtr);
            setFileTypes(pFileDialog, 2, filterMem);

            Marshal.Release(pFileDialog);
            // Note: we intentionally leak the filter strings — they must remain valid for the dialog lifetime
        }
        catch
        {
            // Filter setup failed — dialog will still work, just without filter
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFileTypesDelegate(IntPtr self, uint cFileTypes, IntPtr rgFilterSpec);
}
