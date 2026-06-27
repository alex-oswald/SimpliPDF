using System.Diagnostics.CodeAnalysis;

namespace Windows.Win32;

// CsWin32 runs with allowMarshaling: true (see NativeMethods.json), so the generated
// CoCreateInstance P/Invoke marshals its result as `[MarshalAs(UnmanagedType.IUnknown)] out object`.
// The trimmer / ILC flags that with IL2050 ("correctness of COM interop cannot be guaranteed after
// trimming"). It is safe here: DispatchObject.CoCreate (Interop/Dispatch.cs) only ever activates
// through IUnknown — whose members are never trimmed — and immediately re-wraps the raw pointer on
// the source-generated ComWrappers path, so no specific COM interface type is built-in marshalled.
//
// A raw-pointer overload would sidestep the warning, but allowMarshaling: true (required elsewhere,
// e.g. the native file dialogs) makes CsWin32 emit only the object-marshalling form, and
// hand-written P/Invokes are disallowed in this project. The warning therefore originates inside the
// generated PInvoke class, so the suppression has to live on that (partial) type rather than on the
// DispatchObject caller.
[UnconditionalSuppressMessage(
    "Trimming",
    "IL2050",
    Justification =
        "CoCreateInstance is only ever marshalled through IUnknown and is immediately bridged to " +
        "ComWrappers in DispatchObject.CoCreate; no trimmable COM interface members are involved.")]
internal static partial class PInvoke
{
}
