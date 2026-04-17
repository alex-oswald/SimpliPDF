using System.Runtime.CompilerServices;
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
    public static string[]? OpenPdfFiles(IntPtr hwnd)
    {
        Span<char> buffer = new char[65536];
        ref var bufferRef = ref MemoryMarshal.GetReference(buffer);

        var ofn = new OPENFILENAMEW
        {
            lStructSize = (uint)Unsafe.SizeOf<OPENFILENAMEW>(),
            hwndOwner = new HWND(hwnd),
            nMaxFile = (uint)buffer.Length,
            Flags = OPEN_FILENAME_FLAGS.OFN_EXPLORER
                  | OPEN_FILENAME_FLAGS.OFN_FILEMUSTEXIST
                  | OPEN_FILENAME_FLAGS.OFN_PATHMUSTEXIST
                  | OPEN_FILENAME_FLAGS.OFN_NOCHANGEDIR
                  | OPEN_FILENAME_FLAGS.OFN_ALLOWMULTISELECT,
        };

        // Pin the buffer and filter string for the native call
        var filterStr = "PDF Files\0*.pdf\0All Files\0*.*\0\0";
        var filterPin = GCHandle.Alloc(filterStr.ToCharArray(), GCHandleType.Pinned);
        var bufferPin = GCHandle.Alloc(buffer.ToArray(), GCHandleType.Pinned);
        // We need to work with the pinned array, not the span
        var pinnedBuffer = (char[])bufferPin.Target!;

        try
        {
            SetPwstr(ref ofn.lpstrFile, bufferPin.AddrOfPinnedObject());
            SetPcwstr(ref ofn.lpstrFilter, filterPin.AddrOfPinnedObject());

            if (!PInvoke.GetOpenFileName(ref ofn))
                return null;

            var parts = ParseNullSeparated(pinnedBuffer);
            if (parts.Length == 0) return null;
            if (parts.Length == 1) return [parts[0]];

            var dir = parts[0];
            return parts[1..].Select(f => Path.Combine(dir, f)).ToArray();
        }
        finally
        {
            filterPin.Free();
            bufferPin.Free();
        }
    }

    public static string? SavePdfFile(IntPtr hwnd, string defaultName = "Merged.pdf")
    {
        var buffer = new char[260];
        defaultName.CopyTo(0, buffer, 0, defaultName.Length);

        var filterChars = "PDF Files\0*.pdf\0\0".ToCharArray();
        var defExtChars = "pdf\0".ToCharArray();
        var bufferPin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var filterPin = GCHandle.Alloc(filterChars, GCHandleType.Pinned);
        var defExtPin = GCHandle.Alloc(defExtChars, GCHandleType.Pinned);

        try
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)Unsafe.SizeOf<OPENFILENAMEW>(),
                hwndOwner = new HWND(hwnd),
                nMaxFile = (uint)buffer.Length,
                Flags = OPEN_FILENAME_FLAGS.OFN_EXPLORER
                      | OPEN_FILENAME_FLAGS.OFN_OVERWRITEPROMPT
                      | OPEN_FILENAME_FLAGS.OFN_PATHMUSTEXIST
                      | OPEN_FILENAME_FLAGS.OFN_NOCHANGEDIR,
            };
            SetPwstr(ref ofn.lpstrFile, bufferPin.AddrOfPinnedObject());
            SetPcwstr(ref ofn.lpstrFilter, filterPin.AddrOfPinnedObject());
            SetPcwstr(ref ofn.lpstrDefExt, defExtPin.AddrOfPinnedObject());

            if (!PInvoke.GetSaveFileName(ref ofn))
                return null;

            var end = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, end >= 0 ? end : buffer.Length);
        }
        finally
        {
            bufferPin.Free();
            filterPin.Free();
            defExtPin.Free();
        }
    }

    public static string? PickFolder(IntPtr hwnd)
    {
        var displayName = new char[260];
        var titleChars = "Select output folder\0".ToCharArray();
        var displayPin = GCHandle.Alloc(displayName, GCHandleType.Pinned);
        var titlePin = GCHandle.Alloc(titleChars, GCHandleType.Pinned);

        try
        {
            var bi = new Windows.Win32.UI.Shell.BROWSEINFOW
            {
                hwndOwner = new HWND(hwnd),
                ulFlags = 0x0001 | 0x0040, // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE
            };
            SetPwstr(ref bi.pszDisplayName, displayPin.AddrOfPinnedObject());
            SetPcwstr(ref bi.lpszTitle, titlePin.AddrOfPinnedObject());

            var pidlPtr = SHBrowseForFolderMarshal(ref bi);
            if (pidlPtr == IntPtr.Zero) return null;

            try
            {
                var pathBuffer = new char[260];
                if (SHGetPathFromIDListMarshal(pidlPtr, pathBuffer))
                {
                    var end = Array.IndexOf(pathBuffer, '\0');
                    return new string(pathBuffer, 0, end >= 0 ? end : pathBuffer.Length);
                }
                return null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidlPtr);
            }
        }
        finally
        {
            displayPin.Free();
            titlePin.Free();
        }
    }

    // Marshaled overloads for folder picker — avoids unsafe pointers for ITEMIDLIST*
    [DllImport("shell32.dll", EntryPoint = "SHBrowseForFolderW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderMarshal(ref Windows.Win32.UI.Shell.BROWSEINFOW lpbi);

    [DllImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListMarshal(IntPtr pidl, [MarshalAs(UnmanagedType.LPArray)] char[] pszPath);

    // Helper to set PWSTR/PCWSTR fields from pinned addresses without unsafe
    private static void SetPwstr(ref PWSTR target, IntPtr addr)
        => Unsafe.As<PWSTR, IntPtr>(ref target) = addr;

    private static void SetPcwstr(ref PCWSTR target, IntPtr addr)
        => Unsafe.As<PCWSTR, IntPtr>(ref target) = addr;

    private static string[] ParseNullSeparated(char[] buffer)
    {
        var results = new List<string>();
        int start = 0;
        while (start < buffer.Length)
        {
            int end = Array.IndexOf(buffer, '\0', start);
            if (end < 0 || end == start) break;
            results.Add(new string(buffer, start, end - start));
            start = end + 1;
        }
        return results.ToArray();
    }
}
