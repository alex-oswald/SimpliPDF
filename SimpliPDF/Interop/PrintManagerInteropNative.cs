using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;

namespace SimpliPDF.Interop;

/// <summary>
/// AOT-safe replacement for <c>Windows.Graphics.Printing.PrintManagerInterop.ShowPrintUIForWindowAsync</c>.
///
/// The CsWinRT projection of that method returns <c>IAsyncOperation&lt;bool&gt;</c>. Awaiting it makes
/// CsWinRT cast a bare <c>IInspectable</c> to that generic interface through
/// <see cref="IDynamicInterfaceCastable"/>, which under Native AOT needs a per-instantiation "helper
/// type". The CsWinRT source generator only emits those for instantiations it can see crossing the
/// ABI in the authoring direction; nothing registers the <em>consuming</em> instantiation for
/// <c>IAsyncOperation&lt;bool&gt;</c> (no referenced projection uses it), so on the shipped Native AOT
/// build (x64/ARM64 Release, and the MSI built from it) the await threw:
///
///   System.NotSupportedException: Cannot retrieve a helper type for generic public type
///   'Windows.Foundation.IAsyncOperation`1[System.Boolean]'.
///
/// The user-visible symptom was the Print dialog opening but the preview spinning on "Connecting…"
/// forever — the exception only surfaced in the status bar. It never reproduced on the Debug/JIT inner
/// loop because the throwing path is gated on Native AOT (<c>!RuntimeFeature.IsDynamicCodeCompiled</c>).
///
/// Upstream fixes this by changing the interop projection's IDL (CsWinRT PR #2349, which ships in a
/// newer Windows SDK projection than this app pins). Until that flows in, we drive the interop
/// ourselves through source-generated COM — the same AOT-safe ComWrappers path <see cref="ComInterop"/>
/// and <see cref="DispatchObject"/> already use — and await completion through the <em>non-generic</em>
/// <c>IAsyncInfo</c>, so CsWinRT's generic marshaller (and the missing helper type) is never touched.
/// </summary>
internal static partial class PrintManagerInteropNative
{
    // Windows.Foundation.AsyncStatus.
    private const int AsyncStatusStarted = 0;
    private const int AsyncStatusError = 3;

    private const int PollIntervalMilliseconds = 75;
    private const int EFail = unchecked((int)0x80004005);

    private const string PrintManagerRuntimeClassId = "Windows.Graphics.Printing.PrintManager";

    // IID_IPrintManagerInterop, from printmanagerinterop.h.
    private static readonly Guid IID_IPrintManagerInterop = new("c5435a42-8d43-4e7b-a68a-ef311e392087");

    // Parameterized IID of Windows.Foundation.IAsyncOperation<Boolean> — the interface
    // ShowPrintUIForWindowAsync's riid parameter asks the async operation to be returned as.
    private static readonly Guid IID_IAsyncOperationOfBoolean = new("cdb5efb3-5788-509d-9be1-71ccb8a3362a");

    /// <summary>
    /// Shows the system print UI for <paramref name="hwnd"/> and completes when the async operation
    /// the interop returns finishes — matching the awaited projection method this replaces.
    /// </summary>
    internal static async Task ShowPrintUIForWindowAsync(nint hwnd)
    {
        IAsyncInfoNative asyncOperation = BeginShowPrintUI(hwnd);
        try
        {
            while (true)
            {
                Marshal.ThrowExceptionForHR(asyncOperation.GetStatus(out int status));
                if (status == AsyncStatusStarted)
                {
                    await Task.Delay(PollIntervalMilliseconds);
                    continue;
                }

                if (status == AsyncStatusError)
                {
                    Marshal.ThrowExceptionForHR(asyncOperation.GetErrorCode(out int errorCode));
                    Marshal.ThrowExceptionForHR(errorCode < 0 ? errorCode : EFail);
                }

                return;
            }
        }
        finally
        {
            asyncOperation.Close();
        }
    }

    /// <summary>
    /// Activates the <c>PrintManager</c> activation factory and returns it as the source-generated
    /// <see cref="IPrintManagerInterop"/> wrapper, entirely on the AOT-safe ComWrappers path. Shared
    /// with the headless COM smoke test, which validates this activation without showing any UI.
    /// </summary>
    internal static IPrintManagerInterop AcquireInterop()
    {
        nint factory = GetActivationFactory(PrintManagerRuntimeClassId, IID_IPrintManagerInterop);
        try
        {
            return (IPrintManagerInterop)ComInterop.Wrappers.GetOrCreateObjectForComInstance(
                factory, CreateObjectFlags.None);
        }
        finally
        {
            // GetOrCreateObjectForComInstance takes its own AddRef; drop the reference activation
            // handed us so ownership lives solely with the managed wrapper.
            Marshal.Release(factory);
        }
    }

    private static IAsyncInfoNative BeginShowPrintUI(nint hwnd)
    {
        IPrintManagerInterop interop = AcquireInterop();

        Guid riid = IID_IAsyncOperationOfBoolean;
        Marshal.ThrowExceptionForHR(interop.ShowPrintUIForWindowAsync(hwnd, in riid, out nint asyncOperation));
        if (asyncOperation == 0)
            throw new InvalidOperationException("ShowPrintUIForWindowAsync returned a null async operation.");

        try
        {
            // Re-wrap and cast to the non-generic IAsyncInfo (its QueryInterface succeeds because
            // IAsyncOperation<bool> derives from IAsyncInfo). Awaiting via IAsyncInfo keeps us off the
            // AOT-forbidden IAsyncOperation<bool> helper-type path entirely.
            return (IAsyncInfoNative)ComInterop.Wrappers.GetOrCreateObjectForComInstance(
                asyncOperation, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(asyncOperation);
        }
    }

    private static nint GetActivationFactory(string runtimeClassId, Guid iid)
    {
        PInvoke.WindowsCreateString(runtimeClassId, (uint)runtimeClassId.Length, out WindowsDeleteStringSafeHandle classId)
            .ThrowOnFailure();
        using (classId)
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId.DangerousGetHandle(), in iid, out nint factory));
            if (factory == 0)
                throw new InvalidOperationException($"No activation factory for '{runtimeClassId}'.");
            return factory;
        }
    }

    // Hand-declared for the same reason ComInterop.CoCreateInstance is (see that type's remarks): with
    // allowMarshaling:true CsWin32 emits RoGetActivationFactory as [MarshalAs(IUnknown)] out object,
    // which routes through the built-in COM marshaller Native AOT removes. We need the raw factory
    // pointer so it can flow through the ComWrappers path instead. Declared with LibraryImport so the
    // stub is source-generated and blittable (HSTRING/Guid/nint), and pinned to System32.
    [LibraryImport("combase.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);
}

/// <summary>
/// The activation-factory slice of <c>IPrintManagerInterop</c> (printmanagerinterop.h). Modeled as a
/// source-generated COM interface: the three <c>IInspectable</c> members occupy vtable slots 3-5 so
/// the interop methods land on their real slots (6 and 7).
/// </summary>
[GeneratedComInterface]
[Guid("c5435a42-8d43-4e7b-a68a-ef311e392087")]
internal partial interface IPrintManagerInterop
{
    // Windows.Foundation.IInspectable (vtable slots 3-5) — declared only to reserve the slots.
    [PreserveSig] int GetIids(out uint iidCount, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint className);
    [PreserveSig] int GetTrustLevel(out int trustLevel);

    // IPrintManagerInterop (vtable slots 6-7).
    [PreserveSig] int GetForWindow(nint appWindow, in Guid riid, out nint printManager);
    [PreserveSig] int ShowPrintUIForWindowAsync(nint appWindow, in Guid riid, out nint asyncOperation);
}

/// <summary>
/// The non-generic <c>Windows.Foundation.IAsyncInfo</c> (fixed IID), used to await the print async
/// operation by polling its status without materializing an <c>IAsyncOperation&lt;bool&gt;</c>. The
/// three <c>IInspectable</c> members occupy vtable slots 3-5 so the <c>IAsyncInfo</c> members land on
/// their real slots (6-10).
/// </summary>
[GeneratedComInterface]
[Guid("00000036-0000-0000-c000-000000000046")]
internal partial interface IAsyncInfoNative
{
    // Windows.Foundation.IInspectable (vtable slots 3-5) — declared only to reserve the slots.
    [PreserveSig] int GetIids(out uint iidCount, out nint iids);
    [PreserveSig] int GetRuntimeClassName(out nint className);
    [PreserveSig] int GetTrustLevel(out int trustLevel);

    // Windows.Foundation.IAsyncInfo (vtable slots 6-10).
    [PreserveSig] int GetId(out uint id);
    [PreserveSig] int GetStatus(out int status);
    [PreserveSig] int GetErrorCode(out int errorCode);
    [PreserveSig] int Cancel();
    [PreserveSig] int Close();
}
