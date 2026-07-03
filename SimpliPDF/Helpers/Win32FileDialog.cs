using System.Runtime.InteropServices;
using SimpliPDF.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace SimpliPDF.Helpers;

/// <summary>
/// Win32 file dialogs via source-generated COM (<see cref="SimpliPDF.Interop.IFileDialog"/> and
/// friends) — reliable in unpackaged WinUI 3 apps and Native AOT friendly. Activation goes through
/// the shared ComWrappers helper (see <see cref="SimpliPDF.Interop.ComInterop"/>), so no built-in
/// COM RCW is ever created.
/// </summary>
public static class Win32FileDialog
{
    public static unsafe string[]? OpenPdfFiles(IntPtr hwnd)
    {
        IFileOpenDialog? dialog = ShellDialogFactory.CreateFileOpenDialog();
        if (dialog is null) return null;

        List<nint> filterAllocations = [];
        try
        {
            if (dialog.GetOptions(out uint options) >= 0)
            {
                dialog.SetOptions(options
                    | (uint)FILEOPENDIALOGOPTIONS.FOS_ALLOWMULTISELECT
                    | (uint)FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST);
            }
            dialog.SetTitle("Open PDF Files");
            filterAllocations = ApplyPdfFilter(dialog);

            if (dialog.Show(hwnd) < 0)
                return null;

            if (dialog.GetResults(out IShellItemArray items) < 0 || items is null)
                return null;

            items.GetCount(out uint count);
            List<string> paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                if (items.GetItemAt(i, out IShellItem item) < 0 || item is null)
                    continue;

                string? path = GetFileSystemPath(item);
                if (path is not null)
                    paths.Add(path);
            }
            return paths.Count > 0 ? paths.ToArray() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            FreeAll(filterAllocations);
        }
    }

    public static unsafe string? SavePdfFile(IntPtr hwnd, string defaultName = "Merged.pdf")
    {
        IFileDialog? dialog = ShellDialogFactory.CreateFileSaveDialog();
        if (dialog is null) return null;

        List<nint> filterAllocations = [];
        try
        {
            filterAllocations = ApplyPdfFilter(dialog);
            dialog.SetDefaultExtension("pdf");
            dialog.SetFileName(defaultName);

            if (dialog.Show(hwnd) < 0)
                return null;

            if (dialog.GetResult(out IShellItem item) < 0 || item is null)
                return null;

            return GetFileSystemPath(item);
        }
        catch
        {
            return null;
        }
        finally
        {
            FreeAll(filterAllocations);
        }
    }

    public static unsafe string? PickFolder(IntPtr hwnd)
    {
        IFileOpenDialog? dialog = ShellDialogFactory.CreateFileOpenDialog();
        if (dialog is null) return null;

        try
        {
            if (dialog.GetOptions(out uint options) >= 0)
                dialog.SetOptions(options | (uint)FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS);
            dialog.SetTitle("Select output folder");

            if (dialog.Show(hwnd) < 0)
                return null;

            if (dialog.GetResult(out IShellItem item) < 0 || item is null)
                return null;

            return GetFileSystemPath(item);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Read a shell item's file-system path and free the CoTaskMem string it returns.</summary>
    private static string? GetFileSystemPath(IShellItem item)
    {
        if (item.GetDisplayName(unchecked((uint)SIGDN.SIGDN_FILESYSPATH), out nint path) < 0 || path == 0)
            return null;

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    /// <summary>
    /// Set the PDF + All Files filter. The <see cref="COMDLG_FILTERSPEC"/> array and its strings are
    /// allocated in CoTaskMem and returned so the caller can free them once the dialog has closed.
    /// </summary>
    private static unsafe List<nint> ApplyPdfFilter(IFileDialog dialog)
    {
        (string Name, string Spec)[] filters =
        [
            ("PDF Files", "*.pdf"),
            ("All Files", "*.*"),
        ];

        List<nint> allocations = new List<nint>(filters.Length * 2 + 1);
        nint specArray = Marshal.AllocCoTaskMem(sizeof(COMDLG_FILTERSPEC) * filters.Length);
        allocations.Add(specArray);

        COMDLG_FILTERSPEC* specs = (COMDLG_FILTERSPEC*)specArray;
        for (int i = 0; i < filters.Length; i++)
        {
            nint name = Marshal.StringToCoTaskMemUni(filters[i].Name);
            nint spec = Marshal.StringToCoTaskMemUni(filters[i].Spec);
            allocations.Add(name);
            allocations.Add(spec);
            specs[i].pszName = new PCWSTR((char*)name);
            specs[i].pszSpec = new PCWSTR((char*)spec);
        }

        dialog.SetFileTypes((uint)filters.Length, specs);
        return allocations;
    }

    private static void FreeAll(List<nint> allocations)
    {
        foreach (nint allocation in allocations)
            Marshal.FreeCoTaskMem(allocation);
    }
}
