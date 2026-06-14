using System.Runtime.InteropServices;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SimpliPDF.Interop;

namespace SimpliPDF.Services;

public record ScannerInfo(string DeviceId, string Name);

public record ScannerCapabilities(List<int> SupportedDpi, List<ScanColorMode> SupportedColorModes);

public enum ScanColorMode { Color = 1, Grayscale = 2, BlackAndWhite = 4 }

/// <summary>
/// Crop region expressed as fractions (0–1) of the full scan area.
/// </summary>
public record ScanCropRegion(double Left, double Top, double Right, double Bottom);

/// <summary>
/// WIA-based scanning service. All COM calls run on a dedicated STA thread and use the
/// <see cref="DispatchObject"/> late-binding helper (no <c>dynamic</c>), keeping the service
/// free of the Microsoft.CSharp runtime binder so it is compatible with trimming / Native AOT.
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
            var dm = DispatchObject.CoCreate("WIA.DeviceManager");
            if (dm == null) return result;

            var infos = dm.GetObject("DeviceInfos");
            if (infos == null) return result;

            foreach (var info in infos.EnumerateObjects())
            {
                try
                {
                    int type = info.GetInt("Type");
                    if (type != 1 && type != 2) continue;

                    string id = info.GetString("DeviceID");
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

    private static string GetDeviceName(DispatchObject deviceInfo)
    {
        try
        {
            var props = deviceInfo.GetObject("Properties");
            if (props == null) return "Scanner";

            foreach (var prop in props.EnumerateObjects())
            {
                try
                {
                    if (prop.GetString("Name") == "Name")
                    {
                        string val = prop.GetString("Value");
                        return string.IsNullOrEmpty(val) ? "Scanner" : val;
                    }
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
                var scanItem = FirstItem(device);
                var props = scanItem?.GetObject("Properties");
                if (props != null)
                {
                    dpiValues = ReadSupportedValues(props, 6147);
                    var colorInts = ReadSupportedValues(props, 6146);
                    foreach (var c in colorInts)
                    {
                        if (Enum.IsDefined(typeof(ScanColorMode), c))
                            colorModes.Add((ScanColorMode)c);
                    }
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

    private static List<int> ReadSupportedValues(DispatchObject properties, int propertyId)
    {
        var values = new List<int>();
        try
        {
            var prop = FindProperty(properties, propertyId);
            if (prop == null) return values;

            int subType = prop.GetInt("SubType");

            if (subType == 1) // WiaSubType.wiaSubTypeList
            {
                var subValues = prop.GetObject("SubTypeValues");
                if (subValues != null)
                {
                    foreach (int val in subValues.EnumerateInts())
                        values.Add(val);
                }
            }
            else if (subType == 2) // WiaSubType.wiaSubTypeRange
            {
                int min = prop.GetInt("SubTypeMin");
                int max = prop.GetInt("SubTypeMax");
                int step = prop.GetInt("SubTypeStep");
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

            var scanItem = FirstItem(device);
            if (scanItem == null) return null;

            var props = scanItem.GetObject("Properties");
            if (props != null)
            {
                TrySetProperty(props, 6147, 75);  // Low DPI for fast preview
                TrySetProperty(props, 6148, 75);
                TrySetProperty(props, 6146, 1);   // Color
            }

            try
            {
                var image = scanItem.Call("Transfer", WiaFormatBmp);
                if (image == null) return null;

                var path = Path.Combine(ScratchFolder, $"preview_{Guid.NewGuid():N}.bmp");
                image.Call("SaveFile", path);
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
    /// When a crop region is provided, only the selected area is scanned.
    /// </summary>
    public static Task<string?> ScanAsync(string deviceId, int dpi, ScanColorMode colorMode, ScanCropRegion? crop = null)
    {
        return RunOnStaThread(() =>
        {
            var device = ConnectDevice(deviceId);
            if (device == null)
                throw new InvalidOperationException("Scanner not found. It may have been disconnected.");

            var scanItem = FirstItem(device);
            var props = scanItem?.GetObject("Properties");
            if (scanItem == null || props == null)
                throw new InvalidOperationException("Scanner not found. It may have been disconnected.");

            TrySetProperty(props, 6147, dpi);
            TrySetProperty(props, 6148, dpi);
            TrySetProperty(props, 6146, (int)colorMode);

            // Apply crop region via WIA extent properties
            if (crop != null)
            {
                // Read the full scan area at the current DPI
                int fullWidth = TryGetPropertyValue(props, 6151);
                int fullHeight = TryGetPropertyValue(props, 6152);

                // Fallback: estimate from standard letter size at selected DPI
                if (fullWidth <= 0) fullWidth = (int)(8.5 * dpi);
                if (fullHeight <= 0) fullHeight = (int)(11.0 * dpi);

                int xPos = (int)(crop.Left * fullWidth);
                int yPos = (int)(crop.Top * fullHeight);
                int xExtent = Math.Max(1, (int)((crop.Right - crop.Left) * fullWidth));
                int yExtent = Math.Max(1, (int)((crop.Bottom - crop.Top) * fullHeight));

                // Clamp so position + extent doesn't exceed full size
                if (xPos + xExtent > fullWidth) xExtent = fullWidth - xPos;
                if (yPos + yExtent > fullHeight) yExtent = fullHeight - yPos;

                // Reset position to origin first so shrinking extent can't fail
                TrySetProperty(props, 6149, 0);
                TrySetProperty(props, 6150, 0);
                // Set desired extent
                TrySetProperty(props, 6151, xExtent);
                TrySetProperty(props, 6152, yExtent);
                // Set desired position
                TrySetProperty(props, 6149, xPos);
                TrySetProperty(props, 6150, yPos);
            }

            DispatchObject? image;
            try
            {
                image = scanItem.Call("Transfer", WiaFormatBmp);
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80210006))
            {
                return null;
            }
            if (image == null) return null;

            var imagePath = Path.Combine(ScratchFolder, $"scan_{Guid.NewGuid():N}.bmp");
            image.Call("SaveFile", imagePath);

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

    private static DispatchObject? ConnectDevice(string deviceId)
    {
        var dm = DispatchObject.CoCreate("WIA.DeviceManager");
        if (dm == null) return null;

        var infos = dm.GetObject("DeviceInfos");
        if (infos == null) return null;

        foreach (var info in infos.EnumerateObjects())
        {
            try
            {
                if (info.GetString("DeviceID") == deviceId)
                    return info.Call("Connect");
            }
            catch { }
        }
        return null;
    }

    /// <summary>Return the first scan item of a connected device (WIA Items is 1-based).</summary>
    private static DispatchObject? FirstItem(DispatchObject device)
    {
        var items = device.GetObject("Items");
        if (items == null) return null;

        foreach (var item in items.EnumerateObjects())
            return item;

        return null;
    }

    /// <summary>Find a WIA property in a Properties collection by its numeric PropertyID.</summary>
    private static DispatchObject? FindProperty(DispatchObject properties, int propertyId)
    {
        foreach (var prop in properties.EnumerateObjects())
        {
            try
            {
                if (prop.GetInt("PropertyID") == propertyId)
                    return prop;
            }
            catch { }
        }
        return null;
    }

    private static void TrySetProperty(DispatchObject properties, int propertyId, int value)
    {
        try
        {
            FindProperty(properties, propertyId)?.Set("Value", value);
        }
        catch { }
    }

    private static int TryGetPropertyValue(DispatchObject properties, int propertyId)
    {
        try
        {
            var prop = FindProperty(properties, propertyId);
            return prop?.GetInt("Value") ?? 0;
        }
        catch { return 0; }
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
