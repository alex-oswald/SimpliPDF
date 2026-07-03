using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SimpliPDF.Interop;

/// <summary>
/// COM activation helper for the source-generated (<see cref="GeneratedComInterfaceAttribute"/>)
/// COM used by the scanner (<see cref="DispatchObject"/>) and the Win32 file dialogs
/// (<see cref="ShellDialogFactory"/>).
///
/// Native AOT ships no built-in COM marshaller, so the only AOT-safe way to consume a COM object is
/// the explicit ComWrappers path: take a raw <c>IUnknown*</c> and hand it to
/// <see cref="ComWrappers.GetOrCreateObjectForComInstance(nint, CreateObjectFlags)"/> on a
/// <see cref="StrategyBasedComWrappers"/>; the returned object then casts to our
/// <c>[GeneratedComInterface]</c> types through <see cref="IDynamicInterfaceCastable"/>. This is
/// exactly what <see cref="DispatchObject"/> already does when it re-wraps automation results.
///
/// That path needs a <em>raw pointer</em> from activation. CsWin32 cannot provide one here: with
/// <c>allowMarshaling: true</c> (required elsewhere in the project — see <c>NativeMethods.json</c>)
/// every generated <c>CoCreateInstance</c>/<c>CoCreateInstanceEx</c> overload returns
/// <c>[MarshalAs(UnmanagedType.IUnknown)] out object</c>, i.e. it routes through the very built-in
/// marshaller AOT removes and throws <em>"COM interop requires a ComWrappers instance registered for
/// marshalling"</em>. Registering a marshalling ComWrappers does not rescue it either:
/// <see cref="StrategyBasedComWrappers"/>'s sealed <c>CreateObject</c> refuses the
/// <see cref="CreateObjectFlags.TrackerObject"/> flag the global-marshalling path always supplies.
///
/// So <see cref="CoCreate{T}"/> deliberately hand-declares the one raw-pointer
/// <c>CoCreateInstance</c> P/Invoke that CsWin32 will not emit. This is the single, intentional
/// exception to the project's "activate Win32 through CsWin32, never hand-write P/Invoke" rule; it
/// is the minimum needed to obtain the <c>IUnknown*</c> the AOT-safe ComWrappers path requires.
/// </summary>
internal static partial class ComInterop
{
    /// <summary>
    /// The source-generated ComWrappers behind every wrapper the app creates — both
    /// <see cref="CoCreate{T}"/> below and the explicit
    /// <see cref="ComWrappers.GetOrCreateObjectForComInstance(nint, CreateObjectFlags)"/> calls in
    /// <see cref="DispatchObject"/> that re-wrap automation results, so they share one identity cache.
    /// </summary>
    internal static readonly StrategyBasedComWrappers Wrappers = new();

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");

    /// <summary>
    /// Activate a COM class and return it as a source-generated wrapper of <typeparamref name="T"/>,
    /// staying entirely on the AOT-safe ComWrappers path (no built-in COM marshaller).
    /// </summary>
    /// <typeparam name="T">A <c>[GeneratedComInterface]</c> type the created object implements.</typeparam>
    /// <param name="clsid">The class to create.</param>
    /// <param name="clsContext">A <c>CLSCTX</c> bitmask controlling the activation context.</param>
    /// <returns>The wrapped object, or <see langword="null"/> if activation or the QueryInterface fails.</returns>
    internal static T? CoCreate<T>(in Guid clsid, uint clsContext) where T : class
    {
        // Request IUnknown and let the cast below drive the QueryInterface for T via the
        // source-generated IDynamicInterfaceCastable — never Type.GUID reflection, which is fragile
        // under trimming/AOT.
        Guid iid = IID_IUnknown;
        int hr = CoCreateInstance(in clsid, 0, clsContext, in iid, out nint ppv);
        if (hr < 0 || ppv == 0)
            return null;

        try
        {
            return Wrappers.GetOrCreateObjectForComInstance(ppv, CreateObjectFlags.None) as T;
        }
        finally
        {
            // GetOrCreateObjectForComInstance takes its own AddRef; drop the reference activation
            // handed us so ownership lives solely with the managed wrapper.
            Marshal.Release(ppv);
        }
    }

    // Deliberately hand-written (see the type remarks): CsWin32 under allowMarshaling: true only
    // emits the `out object` form, which is unusable under Native AOT. Declared with LibraryImport so
    // it is source-generated and blittable (Guid/nint) — no runtime marshalling stub — and pinned to
    // System32 to match CsWin32's own generated OLE32 imports.
    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoCreateInstance(
        in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);
}
