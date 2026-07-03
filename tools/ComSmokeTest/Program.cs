using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SimpliPDF.Helpers;
using SimpliPDF.Interop;

namespace SimpliPDF.ComSmokeTest;

/// <summary>
/// Headless smoke test that exercises the two COM paths which fail under Native AOT because it ships
/// no built-in COM marshaller: the Win32 common file dialog (<see cref="Win32FileDialog"/> /
/// <see cref="ShellDialogFactory"/>) and the WIA scanner late binding (<see cref="DispatchObject"/>).
///
/// It never shows UI — it only activates the COM objects and makes one harmless call on each, so
/// it runs deterministically on CI or a developer box. The fix under test is
/// <see cref="ComInterop.CoCreate{T}"/>, which activates from a raw <c>IUnknown</c> pointer wrapped
/// by a <see cref="System.Runtime.InteropServices.Marshalling.StrategyBasedComWrappers"/> instead of
/// the built-in marshaller; without it the same binary throws
/// <em>"COM interop requires a ComWrappers instance registered for marshalling"</em>.
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
}
