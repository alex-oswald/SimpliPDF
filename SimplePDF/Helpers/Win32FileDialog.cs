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
        // Use IFileOpenDialog COM interface for folder picking
        var clsid = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"); // CLSID_FileOpenDialog
        var iid = typeof(Windows.Win32.UI.Shell.IFileOpenDialog).GUID;
        PInvoke.CoCreateInstance(in clsid, null, Windows.Win32.System.Com.CLSCTX.CLSCTX_INPROC_SERVER, in iid, out var ppv);
        if (ppv is not Windows.Win32.UI.Shell.IFileOpenDialog dialog)
            return null;

        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | Windows.Win32.UI.Shell.FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS);
            dialog.SetTitle("Select output folder");

            try { dialog.Show(new HWND(hwnd)); }
            catch { return null; }

            dialog.GetResult(out var item);
            item.GetDisplayName(Windows.Win32.UI.Shell.SIGDN.SIGDN_FILESYSPATH, out var path);
            return path.ToString();
        }
        catch
        {
            return null;
        }
    }

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
