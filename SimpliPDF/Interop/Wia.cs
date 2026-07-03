using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SimpliPDF.Interop;

// Low-level WIA (Windows Image Acquisition) 1.0 COM, used only for the full-resolution scan so the
// transfer can be cancelled mid-pass. The automation library (wiaaut.dll, via DispatchObject) that
// drives everything else offers no way to interrupt Item.Transfer once it starts — the scanner runs
// the whole pass and only then returns. The low-level IWiaDataTransfer.idtGetData call, in contrast,
// hands us an IWiaDataCallback the driver invokes repeatedly during the scan; returning S_FALSE from
// it aborts the transfer, which is the only path to true mid-scan cancellation.
//
// Everything here stays on the project's AOT-safe source-generated COM foundation
// ([GeneratedComInterface] + StrategyBasedComWrappers, see Interop/ComInterop.cs). The vtable
// declarations mirror wia_lh.h exactly (method order == vtable slot); unused methods are still
// declared to reserve their slots. Every parameter is blittable (nint/int/uint) and structs are
// pinned by hand, so the generator never has to marshal PROPVARIANT/PROPSPEC/STGMEDIUM.

[StructLayout(LayoutKind.Sequential)]
internal struct WiaPropSpec
{
    public uint ulKind;   // PRSPEC_PROPID
    public nint union;    // PROPID (low 32 bits)
}

// PROPVARIANT: 24 bytes on x64 — a 2-byte VARTYPE + 6 bytes reserved, then a 16-byte union.
[StructLayout(LayoutKind.Sequential)]
internal struct WiaPropVariant
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public nint val0;   // union offset 0 (lVal / puuid / CA.cElems)
    public nint val1;   // union offset 8 (CA.pElems)
}

// STGMEDIUM: { DWORD tymed; <union pointer>; IUnknown* pUnkForRelease; }. On x64 the union is
// 8-aligned, so 4 bytes of padding follow tymed (inserted automatically for the nint that follows).
[StructLayout(LayoutKind.Sequential)]
internal struct WiaStgMedium
{
    public uint tymed;
    public nint contents;        // lpszFileName / hGlobal / pstm ...
    public nint pUnkForRelease;
}

[GeneratedComInterface]
[Guid("5eb2502a-8cf1-11d1-bf92-0060081ed811")]
internal partial interface IWiaDevMgr
{
    [PreserveSig] int EnumDeviceInfo(int lFlag, out nint ppIEnum);
    [PreserveSig] int CreateDevice(nint bstrDeviceID, out nint ppWiaItemRoot);
}

[GeneratedComInterface]
[Guid("4db1ad10-3391-11d2-9a33-00c04fa36145")]
internal partial interface IWiaItem
{
    [PreserveSig] int GetItemType(out int pItemType);
    [PreserveSig] int AnalyzeItem(int lFlags);
    [PreserveSig] int EnumChildItems(out nint ppIEnumWiaItem);
}

[GeneratedComInterface]
[Guid("5e8383fc-3391-11d2-9a33-00c04fa36145")]
internal partial interface IEnumWiaItem
{
    [PreserveSig] int Next(uint celt, out nint ppIWiaItem, out uint pceltFetched);
}

[GeneratedComInterface]
[Guid("98B5E8A0-29CC-491a-AAC0-E6DB4FDCCEB6")]
internal partial interface IWiaPropertyStorage
{
    [PreserveSig] int ReadMultiple(uint cpspec, nint rgpspec, nint rgpropvar);
    [PreserveSig] int WriteMultiple(uint cpspec, nint rgpspec, nint rgpropvar, uint propidNameFirst);
    [PreserveSig] int DeleteMultiple(uint cpspec, nint rgpspec);
    [PreserveSig] int ReadPropertyNames(uint cpropid, nint rgpropid, nint rglpwstrName);
    [PreserveSig] int WritePropertyNames(uint cpropid, nint rgpropid, nint rglpwstrName);
    [PreserveSig] int DeletePropertyNames(uint cpropid, nint rgpropid);
    [PreserveSig] int Commit(uint grfCommitFlags);
    [PreserveSig] int Revert();
    [PreserveSig] int Enum(out nint ppenum);
    [PreserveSig] int SetTimes(nint pctime, nint patime, nint pmtime);
    [PreserveSig] int SetClass(nint clsid);
    [PreserveSig] int Stat(nint pstatpsstg);
    [PreserveSig] int GetPropertyAttributes(uint cpspec, nint rgpspec, nint rgflags, nint rgpropvar);
}

[GeneratedComInterface]
[Guid("a6cef998-a5b0-11d2-a08f-00c04f72dc3c")]
internal partial interface IWiaDataTransfer
{
    [PreserveSig] int idtGetData(nint pMedium, nint pIWiaDataCallback);
}

[GeneratedComInterface]
[Guid("a558a866-a5b0-11d2-a08f-00c04f72dc3c")]
internal partial interface IWiaDataCallback
{
    [PreserveSig]
    int BandedDataCallback(int lMessage, int lStatus, int lPercentComplete,
        int lOffset, int lLength, int lReserved, int lResLength, nint pbBuffer);
}

/// <summary>
/// Managed <see cref="IWiaDataCallback"/> the WIA driver calls into during a transfer. Returning
/// S_FALSE aborts the scan, so this simply relays a <see cref="CancellationToken"/>: it keeps
/// returning S_OK until cancellation is requested, then returns S_FALSE to stop the transfer.
/// </summary>
[GeneratedComClass]
internal sealed partial class WiaCancelCallback(CancellationToken token) : IWiaDataCallback
{
    private const int S_OK = 0;
    private const int S_FALSE = 1;

    public int BandedDataCallback(int lMessage, int lStatus, int lPercentComplete,
        int lOffset, int lLength, int lReserved, int lResLength, nint pbBuffer)
        => token.IsCancellationRequested ? S_FALSE : S_OK;
}

/// <summary>
/// Performs a cancellable full-resolution WIA scan to a temp BMP file. Returns the file path, or
/// <see langword="null"/> if the transfer was cancelled. Throws on any hard failure (no device,
/// unsupported driver, transfer error) so the caller can fall back to the automation transfer.
/// </summary>
internal static partial class WiaScanner
{
    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint PRSPEC_PROPID = 1;

    private const ushort VT_I4 = 3;
    private const ushort VT_CLSID = 72;
    private const ushort VT_VECTOR = 0x1000;

    private const int TYMED_FILE = 2;

    private const int WIA_PROP_RANGE = 0x10;
    private const int WIA_PROP_LIST = 0x20;
    private const int WIA_RANGE_MAX = 2;   // index of the max within a range attribute vector
    private const int WIA_LIST_VALUES = 2; // index the values start at within a list attribute vector

    private const int WiaItemTypeImage = 0x00000001;
    private const int WiaItemTypeTransfer = 0x00002000;

    private const int WIA_IPA_FORMAT = 4106;
    private const int WIA_IPA_TYMED = 4108;
    private const int WIA_IPS_CUR_INTENT = 6146;
    private const int WIA_IPS_XRES = 6147;
    private const int WIA_IPS_YRES = 6148;
    private const int WIA_IPS_XPOS = 6149;
    private const int WIA_IPS_YPOS = 6150;
    private const int WIA_IPS_XEXTENT = 6151;
    private const int WIA_IPS_YEXTENT = 6152;

    private static readonly Guid CLSID_WiaDevMgr = new("A1F4E726-8CF1-11D1-BF92-0060081ED811");
    private static readonly Guid WiaImgFmt_BMP = new("B96B3CAB-0728-11D3-9D7B-0000F81EF32E");
    private static Guid IID_IWiaDataCallback = new("a558a866-a5b0-11d2-a08f-00c04f72dc3c");

    internal static unsafe string? ScanToBmp(string deviceId, int dpi, int intent,
        (double left, double top, double right, double bottom)? crop, string scratchFolder,
        CancellationToken token)
    {
        IWiaDevMgr mgr = ComInterop.CoCreate<IWiaDevMgr>(in CLSID_WiaDevMgr, CLSCTX_LOCAL_SERVER)
            ?? throw new InvalidOperationException("Unable to create the WIA device manager.");

        nint rootPtr;
        int hr;
        nint bstr = Marshal.StringToBSTR(deviceId);
        try { hr = mgr.CreateDevice(bstr, out rootPtr); }
        finally { Marshal.FreeBSTR(bstr); }
        if (hr < 0 || rootPtr == 0)
            throw new InvalidOperationException($"WIA CreateDevice failed (0x{hr:X8}).");

        IWiaItem root = Wrap<IWiaItem>(rootPtr)
            ?? throw new InvalidOperationException("WIA root item unavailable.");

        IWiaItem scanItem = SelectScanItem(root)
            ?? throw new InvalidOperationException("The scanner exposes no scan item.");

        IWiaPropertyStorage props = (IWiaPropertyStorage)scanItem;

        WriteInt(props, WIA_IPS_CUR_INTENT, intent);
        WriteInt(props, WIA_IPS_XRES, dpi);
        WriteInt(props, WIA_IPS_YRES, dpi);
        WriteInt(props, WIA_IPA_TYMED, TYMED_FILE);
        WriteClsid(props, WIA_IPA_FORMAT, WiaImgFmt_BMP);

        // Establish a clean full-bed baseline (WIA drivers persist item state across connections).
        WriteInt(props, WIA_IPS_XPOS, 0);
        WriteInt(props, WIA_IPS_YPOS, 0);
        int fullW = ReadAttributeMax(props, WIA_IPS_XEXTENT);
        int fullH = ReadAttributeMax(props, WIA_IPS_YEXTENT);

        if (crop is { } c && fullW > 0 && fullH > 0)
        {
            int xExtent = Math.Max(1, (int)((c.right - c.left) * fullW));
            int yExtent = Math.Max(1, (int)((c.bottom - c.top) * fullH));
            int xPos = (int)(c.left * fullW);
            int yPos = (int)(c.top * fullH);
            if (xPos + xExtent > fullW) xExtent = fullW - xPos;
            if (yPos + yExtent > fullH) yExtent = fullH - yPos;

            // Extent before position: an extent's valid range is measured from the current origin.
            WriteInt(props, WIA_IPS_XEXTENT, xExtent);
            WriteInt(props, WIA_IPS_YEXTENT, yExtent);
            WriteInt(props, WIA_IPS_XPOS, xPos);
            WriteInt(props, WIA_IPS_YPOS, yPos);
        }
        else
        {
            if (fullW > 0) WriteInt(props, WIA_IPS_XEXTENT, fullW);
            if (fullH > 0) WriteInt(props, WIA_IPS_YEXTENT, fullH);
        }

        IWiaDataTransfer transfer = (IWiaDataTransfer)scanItem;
        WiaCancelCallback callback = new(token);

        // Build the COM callable wrapper for the callback on the same ComWrappers the rest of the
        // app uses, then QI for the exact interface pointer idtGetData expects.
        nint unk = ComInterop.Wrappers.GetOrCreateComInterfaceForObject(callback, CreateComInterfaceFlags.None);
        nint callbackPtr;
        int qi = Marshal.QueryInterface(unk, in IID_IWiaDataCallback, out callbackPtr);
        Marshal.Release(unk);
        if (qi < 0)
            throw new InvalidOperationException($"WIA callback QueryInterface failed (0x{qi:X8}).");

        WiaStgMedium medium = default;
        medium.tymed = TYMED_FILE;
        try
        {
            hr = transfer.idtGetData((nint)(&medium), callbackPtr);
        }
        finally
        {
            Marshal.Release(callbackPtr);
        }

        if (token.IsCancellationRequested)
        {
            ReleaseMedium(ref medium);
            return null;
        }
        if (hr < 0)
        {
            ReleaseMedium(ref medium);
            throw new InvalidOperationException($"WIA transfer failed (0x{hr:X8}).");
        }

        string? wiaFile = medium.contents != 0 ? Marshal.PtrToStringUni(medium.contents) : null;
        if (string.IsNullOrEmpty(wiaFile) || !File.Exists(wiaFile))
        {
            ReleaseMedium(ref medium);
            throw new InvalidOperationException("WIA transfer produced no image file.");
        }

        string dest = Path.Combine(scratchFolder, $"scan_{Guid.NewGuid():N}.bmp");
        File.Copy(wiaFile, dest, overwrite: true);
        ReleaseMedium(ref medium); // deletes the driver's temp file and frees its filename string
        return dest;
    }

    /// <summary>Enumerates the root item's children and returns the first scan/transfer item.</summary>
    private static IWiaItem? SelectScanItem(IWiaItem root)
    {
        if (root.EnumChildItems(out nint enumPtr) < 0 || enumPtr == 0) return null;
        IEnumWiaItem? enumerator = Wrap<IEnumWiaItem>(enumPtr);
        if (enumerator == null) return null;

        IWiaItem? firstChild = null;
        while (enumerator.Next(1, out nint itemPtr, out uint fetched) == 0 && fetched == 1 && itemPtr != 0)
        {
            IWiaItem? item = Wrap<IWiaItem>(itemPtr);
            if (item == null) continue;
            firstChild ??= item;
            if (item.GetItemType(out int type) == 0 && (type & (WiaItemTypeImage | WiaItemTypeTransfer)) != 0)
                return item;
        }
        return firstChild;
    }

    private static unsafe void WriteInt(IWiaPropertyStorage props, int propId, int value)
    {
        WiaPropSpec spec = new() { ulKind = PRSPEC_PROPID, union = propId };
        WiaPropVariant variant = new() { vt = VT_I4, val0 = value };
        props.WriteMultiple(1, (nint)(&spec), (nint)(&variant), 0);
    }

    private static unsafe void WriteClsid(IWiaPropertyStorage props, int propId, Guid clsid)
    {
        WiaPropSpec spec = new() { ulKind = PRSPEC_PROPID, union = propId };
        WiaPropVariant variant = new() { vt = VT_CLSID, val0 = (nint)(&clsid) };
        props.WriteMultiple(1, (nint)(&spec), (nint)(&variant), 0);
    }

    /// <summary>Reads a property's maximum valid value from its attributes (range max or largest
    /// list value), or 0 when it cannot be determined.</summary>
    private static unsafe int ReadAttributeMax(IWiaPropertyStorage props, int propId)
    {
        WiaPropSpec spec = new() { ulKind = PRSPEC_PROPID, union = propId };
        uint flags = 0;
        WiaPropVariant variant = default;
        int hr = props.GetPropertyAttributes(1, (nint)(&spec), (nint)(&flags), (nint)(&variant));
        if (hr < 0) return 0;

        int result = 0;
        try
        {
            if ((variant.vt & VT_VECTOR) != 0 && variant.val1 != 0)
            {
                int count = (int)variant.val0;
                int* values = (int*)variant.val1;
                if ((flags & WIA_PROP_RANGE) != 0 && count > WIA_RANGE_MAX)
                {
                    result = values[WIA_RANGE_MAX];
                }
                else
                {
                    int start = (flags & WIA_PROP_LIST) != 0 ? WIA_LIST_VALUES : 0;
                    for (int i = start; i < count; i++)
                        if (values[i] > result) result = values[i];
                }
            }
            else if (variant.vt == VT_I4)
            {
                result = (int)variant.val0;
            }
        }
        finally
        {
            PropVariantClear((nint)(&variant));
        }
        return result;
    }

    private static T? Wrap<T>(nint ptr) where T : class
    {
        if (ptr == 0) return null;
        try { return ComInterop.Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None) as T; }
        finally { Marshal.Release(ptr); }
    }

    private static unsafe void ReleaseMedium(ref WiaStgMedium medium)
    {
        fixed (WiaStgMedium* p = &medium)
            ReleaseStgMedium((nint)p);
        medium = default;
    }

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void ReleaseStgMedium(nint pmedium);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int PropVariantClear(nint pvar);
}
