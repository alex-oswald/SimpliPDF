using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Controls.Dialogs;

namespace SimplePDF.Helpers;

/// <summary>
/// Win32 file dialogs via CsWin32 — reliable in unpackaged WinUI 3 apps.
/// </summary>
public static class Win32FileDialog
{
#pragma warning disable CS8500 // Takes address of managed type
    public static unsafe string[]? OpenPdfFiles(IntPtr hwnd)
    {
        var buffer = new char[65536];
        fixed (char* pBuffer = buffer)
        fixed (char* pFilter = "PDF Files\0*.pdf\0All Files\0*.*\0\0")
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = new HWND(hwnd),
                lpstrFilter = new PCWSTR(pFilter),
                lpstrFile = new PWSTR(pBuffer),
                nMaxFile = (uint)buffer.Length,
                Flags = OPEN_FILENAME_FLAGS.OFN_EXPLORER
                      | OPEN_FILENAME_FLAGS.OFN_FILEMUSTEXIST
                      | OPEN_FILENAME_FLAGS.OFN_PATHMUSTEXIST
                      | OPEN_FILENAME_FLAGS.OFN_NOCHANGEDIR
                      | OPEN_FILENAME_FLAGS.OFN_ALLOWMULTISELECT,
            };

            if (!PInvoke.GetOpenFileName(ref ofn))
                return null;

            var raw = new string(pBuffer);
            var parts = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            if (parts.Length == 1) return [parts[0]];

            var dir = parts[0];
            return parts[1..].Select(f => Path.Combine(dir, f)).ToArray();
        }
    }

    public static unsafe string? SavePdfFile(IntPtr hwnd, string defaultName = "Merged.pdf")
    {
        var buffer = new char[260];
        defaultName.CopyTo(0, buffer, 0, defaultName.Length);
        fixed (char* pBuffer = buffer)
        fixed (char* pFilter = "PDF Files\0*.pdf\0\0")
        fixed (char* pDefExt = "pdf")
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = new HWND(hwnd),
                lpstrFilter = new PCWSTR(pFilter),
                lpstrDefExt = new PCWSTR(pDefExt),
                lpstrFile = new PWSTR(pBuffer),
                nMaxFile = (uint)buffer.Length,
                Flags = OPEN_FILENAME_FLAGS.OFN_EXPLORER
                      | OPEN_FILENAME_FLAGS.OFN_OVERWRITEPROMPT
                      | OPEN_FILENAME_FLAGS.OFN_PATHMUSTEXIST
                      | OPEN_FILENAME_FLAGS.OFN_NOCHANGEDIR,
            };

            if (!PInvoke.GetSaveFileName(ref ofn))
                return null;

            return new string(pBuffer).TrimEnd('\0');
        }
    }

    public static unsafe string? PickFolder(IntPtr hwnd)
    {
        var displayName = new char[260];
        fixed (char* pDisplayName = displayName)
        fixed (char* pTitle = "Select output folder")
        {
            var bi = new Windows.Win32.UI.Shell.BROWSEINFOW
            {
                hwndOwner = new HWND(hwnd),
                pszDisplayName = new PWSTR(pDisplayName),
                lpszTitle = new PCWSTR(pTitle),
                ulFlags = 0x0001 | 0x0040, // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE
            };

            var pidl = PInvoke.SHBrowseForFolder(in bi);
            if (pidl == null) return null;

            try
            {
                var path = stackalloc char[260];
                if (PInvoke.SHGetPathFromIDList(pidl, new PWSTR(path)))
                    return new string(path).TrimEnd('\0');
                return null;
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)pidl);
            }
        }
    }
}
