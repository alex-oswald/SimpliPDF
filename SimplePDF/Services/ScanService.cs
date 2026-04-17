using System.Runtime.InteropServices;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace SimplePDF.Services;

public record ScannerInfo(string DeviceId, string Name);

public enum ScanColorMode { Color = 1, Grayscale = 2, BlackAndWhite = 4 }

/// <summary>
/// WIA-based scanning service. All COM calls run on a dedicated STA thread.
/// Scanned PDFs are stored in a session scratch folder and cleaned up on app exit.
/// </summary>
public static class ScanService
{
    private const string WiaFormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";

    private static readonly string ScratchFolder =
        Path.Combine(Path.GetTempPath(), "SimplePDF_scans");

    static ScanService()
    {
        Directory.CreateDirectory(ScratchFolder);
    }

    /// <summary>Clean up scratch folder on app exit.</summary>
    public static void Cleanup()
    {
        try { Directory.Delete(ScratchFolder, recursive: true); } catch { }
    }

    /// <summary>Enumerate available WIA scanners.</summary>
    public static Task<List<ScannerInfo>> GetScannersAsync()
    {
        return RunOnStaThread(() =>
        {
            var result = new List<ScannerInfo>();
            var dmType = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (dmType == null) return result;

            dynamic dm = Activator.CreateInstance(dmType)!;
            foreach (dynamic info in dm.DeviceInfos)
            {
                try
                {
                    int type = (int)info.Type;
                    // Type 1 = Scanner, Type 2 = Camera (some scanners report as this)
                    if (type != 1 && type != 2) continue;

                    string id = info.DeviceID;
                    string name = "Unknown Scanner";
                    try
                    {
                        // Try to get name from properties collection
                        foreach (dynamic prop in info.Properties)
                        {
                            if ((int)prop.PropertyID == 7) // WIA_DIP_DEV_NAME = 7
                            {
                                name = prop.get_Value().ToString();
                                break;
                            }
                        }
                    }
                    catch { }

                    result.Add(new ScannerInfo(id, name));
                }
                catch { /* Skip devices that throw */ }
            }
            return result;
        });
    }

    /// <summary>
    /// Scan a page and return the path to a temp PDF.
    /// </summary>
    public static Task<string?> ScanAsync(string deviceId, int dpi, ScanColorMode colorMode)
    {
        return RunOnStaThread(() =>
        {
            var dmType = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (dmType == null)
                throw new InvalidOperationException("WIA is not available on this system.");

            dynamic dm = Activator.CreateInstance(dmType)!;

            // Find and connect to the selected device
            dynamic? device = null;
            foreach (dynamic info in dm.DeviceInfos)
            {
                if ((string)info.DeviceID == deviceId)
                {
                    device = info.Connect();
                    break;
                }
            }

            if (device == null)
                throw new InvalidOperationException("Scanner not found. It may have been disconnected.");

            dynamic scanItem = device.Items[1];

            // Set properties (best-effort — not all scanners support all properties)
            TrySetProperty(scanItem.Properties, 6147, dpi);  // Horizontal DPI
            TrySetProperty(scanItem.Properties, 6148, dpi);  // Vertical DPI
            TrySetProperty(scanItem.Properties, 6146, (int)colorMode); // Color intent

            dynamic image;
            try
            {
                image = scanItem.Transfer(WiaFormatBmp);
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80210006))
            {
                return null; // WIA_S_NO_DEVICE_AVAILABLE / user cancelled
            }

            // Save scanned image to scratch folder
            var imagePath = Path.Combine(ScratchFolder, $"scan_{Guid.NewGuid():N}.bmp");
            image.SaveFile(imagePath);

            // Convert to single-page PDF
            var pdfPath = Path.Combine(ScratchFolder, $"scan_{Guid.NewGuid():N}.pdf");
            try
            {
                using var doc = new PdfDocument();
                var page = doc.AddPage();

                using var xImage = XImage.FromFile(imagePath);
                page.Width = XUnit.FromPoint(xImage.PointWidth);
                page.Height = XUnit.FromPoint(xImage.PointHeight);

                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);

                doc.Save(pdfPath);
            }
            finally
            {
                try { File.Delete(imagePath); } catch { }
            }

            return (string?)pdfPath;
        });
    }

    private static void TrySetProperty(dynamic properties, int propertyId, int value)
    {
        try
        {
            object propIdObj = propertyId;
            dynamic prop = properties.get_Item(ref propIdObj);
            object val = value;
            prop.set_Value(ref val);
        }
        catch { /* Property not supported by this device */ }
    }

    private static Task<T> RunOnStaThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
