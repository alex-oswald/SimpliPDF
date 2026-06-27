using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace SimpliPDF.Interop;

/// <summary>
/// AOT-compatible late binding over COM automation (IDispatch) objects.
///
/// This replaces C# <c>dynamic</c> for talking to the WIA automation library. <c>dynamic</c>
/// relies on <c>Microsoft.CSharp</c> / the DLR, which needs runtime code generation and is not
/// supported under Native AOT. Everything here is built from source-generated COM
/// (<see cref="GeneratedComInterfaceAttribute"/> + <see cref="StrategyBasedComWrappers"/>) and
/// <see cref="ComVariant"/>, all of which are trim- and AOT-safe.
/// </summary>
[GeneratedComInterface]
[Guid("00020400-0000-0000-c000-000000000046")]
internal unsafe partial interface IDispatch
{
    [PreserveSig] int GetTypeInfoCount(uint* pctinfo);
    [PreserveSig] int GetTypeInfo(uint iTInfo, uint lcid, void** ppTInfo);
    [PreserveSig] int GetIDsOfNames(Guid* riid, ushort** rgszNames, uint cNames, uint lcid, int* rgDispId);
    [PreserveSig]
    int Invoke(
        int dispIdMember, Guid* riid, uint lcid, ushort wFlags,
        DISPPARAMS* pDispParams, ComVariant* pVarResult,
        EXCEPINFO* pExcepInfo, uint* puArgErr);
}

[GeneratedComInterface]
[Guid("00020404-0000-0000-c000-000000000046")]
internal unsafe partial interface IEnumVARIANT
{
    [PreserveSig] int Next(uint celt, ComVariant* rgVar, uint* pCeltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(void** ppEnum);
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPPARAMS
{
    public nint rgvarg;            // VARIANT*
    public nint rgdispidNamedArgs; // int*
    public uint cArgs;
    public uint cNamedArgs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EXCEPINFO
{
    public ushort wCode;
    public ushort wReserved;
    public nint bstrSource;
    public nint bstrDescription;
    public nint bstrHelpFile;
    public uint dwHelpContext;
    public nint pvReserved;
    public nint pfnDeferredFillIn;
    public int scode;
}

/// <summary>
/// A thin, strongly-typed wrapper around an automation <see cref="IDispatch"/> object exposing
/// the small slice of late binding the scanner needs: property get/set, method calls, and
/// collection enumeration.
/// </summary>
internal sealed unsafe class DispatchObject
{
    private const ushort DISPATCH_METHOD = 0x1;
    private const ushort DISPATCH_PROPERTYGET = 0x2;
    private const ushort DISPATCH_PROPERTYPUT = 0x4;
    private const int DISPID_PROPERTYPUT = -3;
    private const int DISPID_NEWENUM = -4;
    private const uint LOCALE_USER_DEFAULT = 0x0400;
    private const int DISP_E_EXCEPTION = unchecked((int)0x80020009);

    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-c000-000000000046");
    private static readonly StrategyBasedComWrappers s_comWrappers = new();

    private readonly IDispatch _disp;

    private DispatchObject(IDispatch disp) => _disp = disp;

    /// <summary>Create an automation object from a ProgID (e.g. "WIA.DeviceManager").</summary>
    public static DispatchObject? CoCreate(string progId)
    {
        if (PInvoke.CLSIDFromProgID(progId, out Guid clsid).Failed)
            return null;

        Guid iid = IID_IDispatch;
        HRESULT hr = PInvoke.CoCreateInstance(
            in clsid, null, CLSCTX.CLSCTX_INPROC_SERVER | CLSCTX.CLSCTX_LOCAL_SERVER, in iid, out object ppv);
        if (hr.Failed || ppv is null)
            return null;

        // CsWin32's CoCreateInstance hands back a classic COM RCW (allowMarshaling: true).
        // Bridge that to a source-generated wrapper via the underlying IUnknown so the rest
        // of the late binding stays on the AOT-safe ComWrappers path.
        nint pUnk = Marshal.GetIUnknownForObject(ppv);
        try
        {
            IDispatch disp = (IDispatch)s_comWrappers.GetOrCreateObjectForComInstance(pUnk, CreateObjectFlags.None);
            return new DispatchObject(disp);
        }
        finally
        {
            Marshal.Release(pUnk);
            Marshal.ReleaseComObject(ppv);
        }
    }

    public int GetInt(string name)
    {
        using ComVariant v = Invoke(name, DISPATCH_PROPERTYGET, default);
        return ToInt32(v);
    }

    public string GetString(string name)
    {
        using ComVariant v = Invoke(name, DISPATCH_PROPERTYGET, default);
        return ToStringValue(v);
    }

    public DispatchObject? GetObject(string name)
    {
        using ComVariant v = Invoke(name, DISPATCH_PROPERTYGET, default);
        return WrapDispatch(v);
    }

    /// <summary>Get an indexed/parameterized property (e.g. <c>collection.Item(index)</c>).</summary>
    public int GetIntArg(string name, params object[] args)
    {
        ComVariant[] variants = ToVariants(args);
        try
        {
            using ComVariant v = Invoke(name, DISPATCH_PROPERTYGET | DISPATCH_METHOD, variants);
            return ToInt32(v);
        }
        finally { DisposeAll(variants); }
    }

    public void Set(string name, int value)
    {
        ComVariant arg = ComVariant.Create(value);
        try
        {
            using ComVariant _ = Invoke(name, DISPATCH_PROPERTYPUT, new[] { arg });
        }
        finally { arg.Dispose(); }
    }

    /// <summary>Invoke a method, optionally returning a wrapped object result.</summary>
    public DispatchObject? Call(string name, params object[] args)
    {
        ComVariant[] variants = ToVariants(args);
        try
        {
            using ComVariant v = Invoke(name, DISPATCH_METHOD, variants);
            return WrapDispatch(v);
        }
        finally { DisposeAll(variants); }
    }

    /// <summary>Enumerate a collection whose elements are themselves automation objects.</summary>
    public IEnumerable<DispatchObject> EnumerateObjects()
    {
        EnumVariant? en = GetEnum();
        if (en is null) yield break;
        while (en.TryNext(out DispatchObject? obj))
        {
            if (obj is not null) yield return obj;
        }
    }

    /// <summary>Enumerate a collection whose elements are integers (e.g. WIA SubTypeValues).</summary>
    public IEnumerable<int> EnumerateInts()
    {
        EnumVariant? en = GetEnum();
        if (en is null) yield break;
        while (en.TryNextInt(out int value, out bool ok))
        {
            if (ok) yield return value;
        }
    }

    private EnumVariant? GetEnum()
    {
        using ComVariant v = InvokeDispId(DISPID_NEWENUM, DISPATCH_PROPERTYGET | DISPATCH_METHOD, default);
        VarEnum vt = v.VarType;
        if (vt is not VarEnum.VT_UNKNOWN and not VarEnum.VT_DISPATCH)
            return null;

        nint p = v.GetRawDataRef<nint>();
        if (p == 0) return null;

        IEnumVARIANT en = (IEnumVARIANT)s_comWrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.None);
        return new EnumVariant(en);
    }

    private ComVariant Invoke(string name, ushort flags, ReadOnlySpan<ComVariant> args)
        => InvokeDispId(GetDispId(name), flags, args, name);

    private int GetDispId(string name)
    {
        fixed (char* pName = name)
        {
            ushort* namePtr = (ushort*)pName;
            int dispId;
            Guid iidNull = Guid.Empty;
            int hr = _disp.GetIDsOfNames(&iidNull, &namePtr, 1, LOCALE_USER_DEFAULT, &dispId);
            if (hr < 0)
                throw new COMException($"GetIDsOfNames('{name}') failed.", hr);
            return dispId;
        }
    }

    private ComVariant InvokeDispId(int dispId, ushort flags, ReadOnlySpan<ComVariant> args, string? name = null)
    {
        // COM expects arguments in reverse order.
        int n = args.Length;
        ComVariant[] rev = new ComVariant[n];
        for (int i = 0; i < n; i++)
            rev[i] = args[n - 1 - i];

        fixed (ComVariant* pArgs = rev)
        {
            int namedPut = DISPID_PROPERTYPUT;
            DISPPARAMS dp = new DISPPARAMS
            {
                rgvarg = (nint)pArgs,
                cArgs = (uint)n,
            };
            if ((flags & DISPATCH_PROPERTYPUT) != 0)
            {
                dp.rgdispidNamedArgs = (nint)(&namedPut);
                dp.cNamedArgs = 1;
            }

            ComVariant result = default;
            EXCEPINFO ei = default;
            Guid iidNull = Guid.Empty;
            int hr = _disp.Invoke(dispId, &iidNull, LOCALE_USER_DEFAULT, flags, &dp, &result, &ei, null);
            if (hr < 0)
            {
                int scode = hr == DISP_E_EXCEPTION && ei.scode != 0 ? ei.scode : hr;
                FreeBstr(ref ei.bstrSource);
                FreeBstr(ref ei.bstrDescription);
                FreeBstr(ref ei.bstrHelpFile);
                throw new COMException($"IDispatch.Invoke('{name ?? dispId.ToString()}') failed.", scode);
            }
            return result;
        }
    }

    private static DispatchObject? WrapDispatch(ComVariant v)
    {
        VarEnum vt = v.VarType;
        if (vt is not VarEnum.VT_DISPATCH and not VarEnum.VT_UNKNOWN)
            return null;

        nint p = v.GetRawDataRef<nint>();
        if (p == 0) return null;

        IDispatch disp = (IDispatch)s_comWrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.None);
        return new DispatchObject(disp);
    }

    private static ComVariant[] ToVariants(object[] args)
    {
        ComVariant[] arr = new ComVariant[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            arr[i] = args[i] switch
            {
                int n => ComVariant.Create(n),
                string s => ComVariant.Create(s),
                bool b => ComVariant.Create(b),
                _ => throw new NotSupportedException($"Unsupported COM argument type: {args[i]?.GetType()}"),
            };
        }
        return arr;
    }

    private static void DisposeAll(ComVariant[] variants)
    {
        foreach (ComVariant v in variants)
            v.Dispose();
    }

    private static int ToInt32(ComVariant v) => v.VarType switch
    {
        VarEnum.VT_I4 => v.GetRawDataRef<int>(),
        VarEnum.VT_I2 => v.GetRawDataRef<short>(),
        VarEnum.VT_UI4 => (int)v.GetRawDataRef<uint>(),
        VarEnum.VT_UI2 => v.GetRawDataRef<ushort>(),
        VarEnum.VT_I1 => v.GetRawDataRef<sbyte>(),
        VarEnum.VT_UI1 => v.GetRawDataRef<byte>(),
        VarEnum.VT_INT => v.GetRawDataRef<int>(),
        VarEnum.VT_UINT => (int)v.GetRawDataRef<uint>(),
        VarEnum.VT_R8 => (int)v.GetRawDataRef<double>(),
        VarEnum.VT_R4 => (int)v.GetRawDataRef<float>(),
        VarEnum.VT_BOOL => v.GetRawDataRef<short>() != 0 ? 1 : 0,
        _ => 0,
    };

    private static string ToStringValue(ComVariant v)
    {
        if (v.VarType != VarEnum.VT_BSTR)
            return "";
        nint p = v.GetRawDataRef<nint>();
        return p == 0 ? "" : Marshal.PtrToStringBSTR(p) ?? "";
    }

    private static void FreeBstr(ref nint bstr)
    {
        if (bstr != 0)
        {
            Marshal.FreeBSTR(bstr);
            bstr = 0;
        }
    }

    /// <summary>Wraps an <see cref="IEnumVARIANT"/> so the unsafe Next() loop stays out of iterators.</summary>
    private sealed class EnumVariant
    {
        private readonly IEnumVARIANT _en;
        public EnumVariant(IEnumVARIANT en) => _en = en;

        public bool TryNext(out DispatchObject? obj)
        {
            ComVariant v = default;
            uint fetched = 0;
            int hr = _en.Next(1, &v, &fetched);
            if (hr < 0 || fetched == 0)
            {
                v.Dispose();
                obj = null;
                return false;
            }
            obj = WrapDispatch(v);
            v.Dispose();
            return true;
        }

        public bool TryNextInt(out int value, out bool ok)
        {
            ComVariant v = default;
            uint fetched = 0;
            int hr = _en.Next(1, &v, &fetched);
            if (hr < 0 || fetched == 0)
            {
                v.Dispose();
                value = 0;
                ok = false;
                return false;
            }
            value = ToInt32(v);
            v.Dispose();
            ok = true;
            return true;
        }
    }
}
