using System.Runtime.InteropServices;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace SimpliPDF.Services;

public record ScannerInfo(string DeviceId, string Name);

public record ScannerCapabilities(List<int> SupportedDpi, List<ScanColorMode> SupportedColorModes);

public enum ScanColorMode { Color = 1, Grayscale = 2, BlackAndWhite = 4 }

/// <summary>
/// WIA-based scanning service. All COM calls run on a dedicated STA thread.
/// Scanned PDFs are stored in a session scratch folder and cleaned up on app exit.
/// </summary>
public static class ScanService
{
    private const string WiaFormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";

    private static readonly string ScratchFolder =
        Path.Combine(Path.GetTempPath(), "SimpliPDF_scans");

    static ScanService()
    {
        Directory.CreateDirectory(ScratchFolder);
    }

    public static void Cleanup()
    {
        try { Directory.Delete(ScratchFolder, recursive: true); } catch { }
    }

    // --- Prefetch cache ---

    private static Task<List<ScannerInfo>>? _scannersTask;
    private static readonly Dictionary<string, Task<ScannerCapabilities>> _capsCache = [];

    /// <summary>Start prefetching scanner list and capabilities in the background.</summary>
    public static void Prefetch()
    {
        _ = GetScannersAsync();
    }

    public static Task<List<ScannerInfo>> GetScannersAsync()
    {
        // Return cached task if already in flight or completed
        if (_scannersTask != null) return _scannersTask;

        _scannersTask = FetchScannersAndCacheCapabilities();
        return _scannersTask;
    }

    private static async Task<List<ScannerInfo>> FetchScannersAndCacheCapabilities()
    {
        var scanners = await RunOnStaThread(() =>
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
                    if (type != 1 && type != 2) continue;

                    string id = info.DeviceID;
                    string name = GetDeviceName(info);
                    result.Add(new ScannerInfo(id, name));
                }
                catch { }
            }
            return result;
        });

        // Kick off capabilities queries for all found scanners
        foreach (var scanner in scanners)
            _ = GetCapabilitiesAsync(scanner.DeviceId);

        return scanners;
    }

    /// <summary>Invalidate the cache so the next call re-queries devices.</summary>
    public static void InvalidateCache()
    {
        _scannersTask = null;
        lock (_capsCache) _capsCache.Clear();
    }

    private static string GetDeviceName(dynamic deviceInfo)
    {
        try
        {
            foreach (dynamic prop in deviceInfo.Properties)
            {
                try
                {
                    string pname = prop.Name?.ToString() ?? "";
                    if (pname == "Name")
                        return ((object)prop.Value)?.ToString() ?? "Scanner";
                }
                catch { }
            }
        }
        catch { }

        return "Scanner";
    }

    /// <summary>Query supported DPI values and color modes from the scanner.</summary>
    public static Task<ScannerCapabilities> GetCapabilitiesAsync(string deviceId)
    {
        lock (_capsCache)
        {
            if (_capsCache.TryGetValue(deviceId, out var cached))
                return cached;
        }

        var task = RunOnStaThread(() =>
        {
            var dpiValues = new List<int>();
            var colorModes = new List<ScanColorMode>();

            var device = ConnectDevice(deviceId);
            if (device == null)
                return new ScannerCapabilities([300], [ScanColorMode.Color]);

            try
            {
                dynamic scanItem = device.Items[1];
                dpiValues = ReadSupportedValues(scanItem.Properties, 6147);
                var colorInts = ReadSupportedValues(scanItem.Properties, 6146);
                foreach (var c in colorInts)
                {
                    if (Enum.IsDefined(typeof(ScanColorMode), c))
                        colorModes.Add((ScanColorMode)c);
                }
            }
            catch { }

            if (dpiValues.Count == 0) dpiValues = [150, 300, 600];
            if (colorModes.Count == 0) colorModes = [ScanColorMode.Color, ScanColorMode.Grayscale, ScanColorMode.BlackAndWhite];

            return new ScannerCapabilities(dpiValues, colorModes);
        });

        lock (_capsCache)
        {
            _capsCache.TryAdd(deviceId, task);
        }

        return task;
    }

    private static List<int> ReadSupportedValues(dynamic properties, int propertyId)
    {
        var values = new List<int>();
        try
        {
            dynamic? prop = null;
            foreach (dynamic p in properties)
            {
                if ((int)p.PropertyID == propertyId)
                {
                    prop = p;
                    break;
                }
            }
            if (prop == null) return values;

            int subType = (int)prop.SubType;

            if (subType == 1) // WiaSubType.wiaSubTypeList
            {
                foreach (object val in prop.SubTypeValues)
                    values.Add(Convert.ToInt32(val));
            }
            else if (subType == 2) // WiaSubType.wiaSubTypeRange
            {
                int min = (int)prop.SubTypeMin;
                int max = (int)prop.SubTypeMax;
                int step = (int)prop.SubTypeStep;
                if (step <= 0) step = 1;
                for (int v = min; v <= max; v += step)
                    values.Add(v);
                // If the range produces too many entries, pick common values within range
                if (values.Count > 20)
                {
                    var common = new[] { 75, 100, 150, 200, 300, 600, 1200 };
                    values = common.Where(d => d >= min && d <= max).ToList();
                    if (values.Count == 0) values = [min, max];
                }
            }
        }
        catch { }
        return values;
    }

    /// <summary>
    /// Scan a low-resolution preview and return the temp image path.
    /// </summary>
    public static Task<string?> PreviewAsync(string deviceId)
    {
        return RunOnStaThread(() =>
        {
            var device = ConnectDevice(deviceId);
            if (device == null) return null;

            dynamic scanItem = device.Items[1];
            TrySetProperty(scanItem.Properties, 6147, 75);  // Low DPI for fast preview
            TrySetProperty(scanItem.Properties, 6148, 75);
            TrySetProperty(scanItem.Properties, 6146, 1);   // Color

            try
            {
                dynamic image = scanItem.Transfer(WiaFormatBmp);
                var path = Path.Combine(ScratchFolder, $"preview_{Guid.NewGuid():N}.bmp");
                image.SaveFile(path);
                return (string?)path;
            }
            catch (COMException)
            {
                return null;
            }
        });
    }

    /// <summary>
    /// Full-resolution scan, returns path to a temp PDF.
    /// </summary>
    public static Task<string?> ScanAsync(string deviceId, int dpi, ScanColorMode colorMode)
    {
        return RunOnStaThread(() =>
        {
            var device = ConnectDevice(deviceId);
            if (device == null)
                throw new InvalidOperationException("Scanner not found. It may have been disconnected.");

            dynamic scanItem = device.Items[1];
            TrySetProperty(scanItem.Properties, 6147, dpi);
            TrySetProperty(scanItem.Properties, 6148, dpi);
            TrySetProperty(scanItem.Properties, 6146, (int)colorMode);

            dynamic image;
            try
            {
                image = scanItem.Transfer(WiaFormatBmp);
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80210006))
            {
                return null;
            }

            var imagePath = Path.Combine(ScratchFolder, $"scan_{Guid.NewGuid():N}.bmp");
            image.SaveFile(imagePath);

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

    private static dynamic? ConnectDevice(string deviceId)
    {
        var dmType = Type.GetTypeFromProgID("WIA.DeviceManager");
        if (dmType == null) return null;

        dynamic dm = Activator.CreateInstance(dmType)!;
        foreach (dynamic info in dm.DeviceInfos)
        {
            if ((string)info.DeviceID == deviceId)
                return info.Connect();
        }
        return null;
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
        catch { }
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
