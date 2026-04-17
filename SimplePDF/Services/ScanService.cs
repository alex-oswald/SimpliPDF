using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Windows.Storage.Streams;

namespace SimplePDF.Services;

/// <summary>
/// Scans an image using the WIA (Windows Image Acquisition) system dialog,
/// converts it to a single-page PDF saved to temp storage, and returns the path.
/// </summary>
public static class ScanService
{
    // WIA FormatID for BMP (most reliable for scanners)
    private const string WiaFormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";

    /// <summary>
    /// Shows the system scanner dialog and returns the path to a temp PDF
    /// containing the scanned image, or null if cancelled.
    /// </summary>
    public static async Task<string?> ScanToTempPdfAsync()
    {
        // WIA COM calls must run on an STA thread
        var tcs = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(ScanToTempPdfCore());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return await tcs.Task;
    }

    private static string? ScanToTempPdfCore()
    {
        // Late-bind to WIA COM to avoid needing a COM assembly reference
        var wiaDialogType = Type.GetTypeFromProgID("WIA.CommonDialog");
        if (wiaDialogType == null)
            throw new InvalidOperationException("WIA is not available on this system.");

        dynamic dialog = Activator.CreateInstance(wiaDialogType)!;

        // ShowAcquireImage(DeviceType, Intent, Bias, FormatID, AlwaysSelectDevice, UseCommonUI, CancelError)
        // DeviceType 1 = Scanner, Intent 1 = Color, Bias 2 = MaximizeQuality
        dynamic? image;
        try
        {
            image = dialog.ShowAcquireImage(1, 1, 2, WiaFormatBmp, false, true, false);
        }
        catch (COMException)
        {
            return null; // User cancelled or no scanner
        }

        if (image == null) return null;

        // Save scanned image to temp file
        var tempImagePath = Path.Combine(Path.GetTempPath(), $"SimplePDF_scan_{Guid.NewGuid():N}.bmp");
        image.SaveFile(tempImagePath);

        // Convert the scanned image to a single-page PDF
        var tempPdfPath = Path.Combine(Path.GetTempPath(), $"SimplePDF_scan_{Guid.NewGuid():N}.pdf");
        try
        {
            using var doc = new PdfDocument();
            var page = doc.AddPage();

            using var xImage = XImage.FromFile(tempImagePath);
            // Set page size to match image aspect ratio at 72 DPI base
            page.Width = xImage.PointWidth;
            page.Height = xImage.PointHeight;

            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);

            doc.Save(tempPdfPath);
        }
        finally
        {
            // Clean up the intermediate image file
            try { File.Delete(tempImagePath); } catch { }
        }

        return tempPdfPath;
    }
}
