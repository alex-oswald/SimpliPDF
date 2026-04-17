using System.Runtime.InteropServices;

namespace PDFPro.Helpers;

/// <summary>
/// Win32 file dialogs — reliable in unpackaged WinUI 3 apps unlike the WinRT pickers.
/// </summary>
public static class Win32FileDialog
{
    public static string[]? OpenPdfFiles(IntPtr hwnd)
    {
        return OpenFiles(hwnd, "PDF Files\0*.pdf\0All Files\0*.*\0\0", multiSelect: true);
    }

    public static string? SavePdfFile(IntPtr hwnd, string defaultName = "Merged.pdf")
    {
        return SaveFile(hwnd, "PDF Files\0*.pdf\0\0", defaultName, ".pdf");
    }

    public static string? PickFolder(IntPtr hwnd)
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = hwnd,
            lpszTitle = "Select output folder",
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
        };

        var pidl = SHBrowseForFolder(ref bi);
        if (pidl == IntPtr.Zero) return null;

        try
        {
            var path = new char[260];
            if (SHGetPathFromIDList(pidl, path))
                return new string(path).TrimEnd('\0');
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static string[]? OpenFiles(IntPtr hwnd, string filter, bool multiSelect)
    {
        var ofn = new OPENFILENAME();
        ofn.lStructSize = Marshal.SizeOf(ofn);
        ofn.hwndOwner = hwnd;
        ofn.lpstrFilter = filter;
        ofn.nMaxFile = 65536;
        ofn.lpstrFile = new string('\0', ofn.nMaxFile);
        ofn.Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
        if (multiSelect) ofn.Flags |= OFN_ALLOWMULTISELECT;

        if (!GetOpenFileName(ref ofn)) return null;

        // Parse result: if multi-select, first string is directory, rest are filenames
        var raw = ofn.lpstrFile;
        var parts = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) return null;
        if (parts.Length == 1) return [parts[0]];

        // Multi-select: parts[0] = directory, parts[1..] = filenames
        var dir = parts[0];
        return parts[1..].Select(f => Path.Combine(dir, f)).ToArray();
    }

    private static string? SaveFile(IntPtr hwnd, string filter, string defaultName, string defaultExt)
    {
        var ofn = new OPENFILENAME();
        ofn.lStructSize = Marshal.SizeOf(ofn);
        ofn.hwndOwner = hwnd;
        ofn.lpstrFilter = filter;
        ofn.lpstrDefExt = defaultExt.TrimStart('.');
        ofn.nMaxFile = 260;
        // Pre-fill with default name
        ofn.lpstrFile = defaultName + new string('\0', ofn.nMaxFile - defaultName.Length);
        ofn.Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;

        if (!GetSaveFileName(ref ofn)) return null;
        return ofn.lpstrFile.TrimEnd('\0');
    }

    // P/Invoke
    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_ALLOWMULTISELECT = 0x00000200;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OPENFILENAME lpofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileName(ref OPENFILENAME lpofn);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, [MarshalAs(UnmanagedType.LPArray)] char[] pszPath);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public string pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }
}
