using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SimpliPDF.Helpers;
using SimpliPDF.Interop;

namespace SimpliPDF.ComSmokeTest;

/// <summary>
/// Headless smoke test that exercises the COM paths which fail under Native AOT because it ships
/// no built-in COM marshaller: the Win32 common file dialog (<see cref="Win32FileDialog"/> /
/// <see cref="ShellDialogFactory"/>), the WIA scanner late binding (<see cref="DispatchObject"/>),
/// and the print interop (<see cref="PrintManagerInteropNative"/>).
///
/// It never shows UI — it activates the COM objects and makes one harmless call on each (the print
/// check uses an invalid window handle so <c>ShowPrintUIForWindowAsync</c> fails fast instead of
/// showing the dialog), so it runs deterministically on CI or a developer box. The fixes under test
/// wrap raw <c>IUnknown</c> pointers with a
/// <see cref="System.Runtime.InteropServices.Marshalling.StrategyBasedComWrappers"/> and use
/// <c>[GeneratedComInterface]</c> instead of the built-in marshaller / CsWinRT generic projections;
/// without them the same binary throws
/// <em>"COM interop requires a ComWrappers instance registered for marshalling"</em> or
/// <em>"Cannot retrieve a helper type for generic public type IAsyncOperation`1[Boolean]"</em>.
/// Exit code 0 = all paths OK, non-zero = a COM/marshalling failure.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string runtime = RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT";
        Console.WriteLine($"SimpliPDF COM smoke test  [{RuntimeInformation.ProcessArchitecture}, {runtime}]");
        Console.WriteLine(new string('-', 60));

        int failures = 0;
        failures += RunCheck("Win32 file dialog (IFileOpenDialog)", CheckFileDialog);
        failures += RunCheck("WIA scanner (WIA.DeviceManager)", CheckScanner);
        failures += RunCheck("Print interop (IPrintManagerInterop)", CheckPrintInterop);
        failures += RunCheck("Print interop (ShowPrintUIForWindowAsync slot)", CheckPrintShowUiSlot);

        Console.WriteLine(new string('-', 60));
        if (failures == 0)
        {
            Console.WriteLine("RESULT: PASS — COM activation works on this runtime.");
            return 0;
        }

        Console.WriteLine($"RESULT: FAIL — {failures} COM path(s) broken on this runtime.");
        return 1;
    }

    private static int RunCheck(string label, Func<string> check)
    {
        try
        {
            string detail = check();
            Console.WriteLine($"  PASS  {label}: {detail}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {label}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Activates a FileOpenDialog through the source-generated COM path and reads its options.
    /// No <c>Show</c> call, so nothing is displayed. A missing ComWrappers registration surfaces
    /// as a thrown exception during activation.
    /// </summary>
    private static string CheckFileDialog()
    {
        IFileOpenDialog? dialog = ShellDialogFactory.CreateFileOpenDialog();
        if (dialog is null)
            throw new InvalidOperationException("CreateFileOpenDialog returned null (CoCreateInstance failed).");

        int hr = dialog.GetOptions(out uint options);
        if (hr < 0)
            throw new COMException("IFileOpenDialog.GetOptions failed.", hr);

        return $"activated via ComWrappers, options=0x{options:X}";
    }

    /// <summary>
    /// Runs the WIA activation + enumeration exactly as the scanner service does. Zero scanners is
    /// a valid PASS (many machines have none); only a thrown marshalling error is a failure. If the
    /// WIA server itself is unavailable the activation still ran without a ComWrappers error, which
    /// is the thing under test.
    /// </summary>
    private static string CheckScanner()
    {
        DispatchObject? deviceManager = DispatchObject.CoCreate("WIA.DeviceManager");
        if (deviceManager is null)
            return "activation path ran without a marshalling error (WIA.DeviceManager unavailable on this machine)";

        DispatchObject? deviceInfos = deviceManager.GetObject("DeviceInfos");
        int count = 0;
        if (deviceInfos is not null)
        {
            foreach (DispatchObject _ in deviceInfos.EnumerateObjects())
                count++;
        }

        return $"activated + enumerated {count} device(s)";
    }

    /// <summary>
    /// Activates the <c>PrintManager</c> interop factory through the source-generated COM path the
    /// AOT print fix relies on (<see cref="PrintManagerInteropNative"/>). It never calls
    /// <c>ShowPrintUIForWindowAsync</c> — that would show the print UI — so nothing is displayed;
    /// acquiring the factory and casting it to the <c>[GeneratedComInterface]</c> interface already
    /// exercises RoGetActivationFactory, the HSTRING creation, the IID, and the ComWrappers cast that
    /// broke awaiting the projection method under Native AOT.
    /// </summary>
    private static string CheckPrintInterop()
    {
        IPrintManagerInterop interop = PrintManagerInteropNative.AcquireInterop();
        if (interop is null)
            throw new InvalidOperationException("AcquireInterop returned null (factory activation failed).");

        return "activation factory acquired + cast via ComWrappers";
    }

    /// <summary>
    /// Drives the full native <c>ShowPrintUIForWindowAsync</c> call (vtable slot 7 of
    /// <c>IPrintManagerInterop</c>) with a deliberately invalid window handle. The interop validates
    /// the handle and fails synchronously with <c>E_HANDLE</c> ("Invalid window handle") before it can
    /// return an async operation, so no print UI is ever shown and the call returns immediately. A
    /// thrown <see cref="COMException"/> therefore proves the method is reachable at the expected slot
    /// and that its <c>in Guid riid</c> / <c>out</c> pointer signature marshals correctly — the piece
    /// that CsWinRT's generic <c>IAsyncOperation&lt;bool&gt;</c> marshalling broke under Native AOT.
    /// </summary>
    private static string CheckPrintShowUiSlot()
    {
        System.Threading.Tasks.Task task = PrintManagerInteropNative.ShowPrintUIForWindowAsync(0);
        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("ShowPrintUIForWindowAsync did not complete within 8s.");
        }
        catch (AggregateException ex) when (ex.InnerException is COMException com)
        {
            return $"reached slot, failed fast as expected: 0x{com.HResult:X8} {com.Message.Trim()}";
        }

        throw new InvalidOperationException(
            "ShowPrintUIForWindowAsync(0) completed without throwing; expected E_HANDLE for an invalid window.");
    }
}
