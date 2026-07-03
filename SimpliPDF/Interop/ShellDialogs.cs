using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32.System.Com;

namespace SimpliPDF.Interop;

/// <summary>
/// Source-generated (<see cref="GeneratedComInterfaceAttribute"/>) COM interfaces for the Win32
/// common file dialogs, mirroring <see cref="DispatchObject"/>'s approach for the scanner.
///
/// CsWin32 emits these shell interfaces as classic <c>[ComImport]</c> types (built-in marshaller),
/// which Native AOT cannot dispatch. Declaring them as <c>[GeneratedComInterface]</c> keeps them on
/// the AOT-safe <see cref="StrategyBasedComWrappers"/> path: objects activated through
/// <see cref="ShellDialogFactory"/> cast to these interfaces via <see cref="IDynamicInterfaceCastable"/>.
///
/// Only the members the app actually calls carry real signatures; every other vtable slot is a
/// correctly ordered placeholder so the ones we use land at the right index. Interface hierarchy and
/// slot order match shobjidl_core.h exactly.
/// </summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("b4db1657-70d7-485e-8e3e-6fcb5a5c1802")]
internal unsafe partial interface IModalWindow
{
    [PreserveSig] int Show(nint hwndOwner);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
internal unsafe partial interface IFileDialog : IModalWindow
{
    [PreserveSig] int SetFileTypes(uint cFileTypes, void* rgFilterSpec);
    [PreserveSig] int SetFileTypeIndex(uint iFileType);
    [PreserveSig] int GetFileTypeIndex(uint* piFileType);
    [PreserveSig] int Advise(nint pfde, uint* pdwCookie);
    [PreserveSig] int Unadvise(uint dwCookie);
    [PreserveSig] int SetOptions(uint fos);
    [PreserveSig] int GetOptions(out uint pfos);
    [PreserveSig] int SetDefaultFolder(nint psi);
    [PreserveSig] int SetFolder(nint psi);
    [PreserveSig] int GetFolder(nint* ppsi);
    [PreserveSig] int GetCurrentSelection(nint* ppsi);
    [PreserveSig] int SetFileName(string pszName);
    [PreserveSig] int GetFileName(nint pszName);
    [PreserveSig] int SetTitle(string pszTitle);
    [PreserveSig] int SetOkButtonLabel(string pszText);
    [PreserveSig] int SetFileNameLabel(string pszLabel);
    [PreserveSig] int GetResult(out IShellItem ppsi);
    [PreserveSig] int AddPlace(nint psi, int fdap);
    [PreserveSig] int SetDefaultExtension(string pszDefaultExtension);
    [PreserveSig] int Close(int hr);
    [PreserveSig] int SetClientGuid(Guid* guid);
    [PreserveSig] int ClearClientData();
    [PreserveSig] int SetFilter(nint pFilter);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
internal unsafe partial interface IFileOpenDialog : IFileDialog
{
    [PreserveSig] int GetResults(out IShellItemArray ppenum);
}

[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal unsafe partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint pbc, Guid* bhid, Guid* riid, void** ppv);
    [PreserveSig] int GetParent(nint* ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out nint ppszName);
}

[GeneratedComInterface]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
internal unsafe partial interface IShellItemArray
{
    [PreserveSig] int BindToHandler(nint pbc, Guid* bhid, Guid* riid, void** ppvOut);
    [PreserveSig] int GetPropertyStore(uint flags, Guid* riid, void** ppv);
    [PreserveSig] int GetPropertyDescriptionList(void* keyType, Guid* riid, void** ppv);
    [PreserveSig] int GetAttributes(uint attribFlags, uint nMask, uint* psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
}

/// <summary>Activates the shell file-dialog COM objects through the shared ComWrappers.</summary>
internal static class ShellDialogFactory
{
    private static readonly Guid CLSID_FileOpenDialog = new("dc1c5a9c-e88a-4dde-a5a1-60f82a20aef7");
    private static readonly Guid CLSID_FileSaveDialog = new("c0b4e2f3-ba21-4773-8dba-335ec946eb8b");

    internal static IFileOpenDialog? CreateFileOpenDialog() =>
        CoCreate<IFileOpenDialog>(CLSID_FileOpenDialog);

    /// <summary>
    /// A FileSaveDialog exposed through its <see cref="IFileDialog"/> base — the app only needs the
    /// members shared with the open dialog, so no save-specific interface is required.
    /// </summary>
    internal static IFileDialog? CreateFileSaveDialog() =>
        CoCreate<IFileDialog>(CLSID_FileSaveDialog);

    private static T? CoCreate<T>(Guid clsid) where T : class =>
        // Activate on the AOT-safe ComWrappers path (see ComInterop.CoCreate): the raw IUnknown* is
        // wrapped and cast to the [GeneratedComInterface] type via IDynamicInterfaceCastable.
        ComInterop.CoCreate<T>(in clsid, (uint)CLSCTX.CLSCTX_INPROC_SERVER);
}
