using Microsoft.UI.Xaml.Media.Imaging;
using SimpliPDF.Models;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SimpliPDF.Helpers;

public static class ThumbnailHelper
{
    /// <summary>Default DPI used when rasterizing a cropped page for saving.</summary>
    public const double RasterizeDpi = 200;

    /// <summary>
    /// Renders a page to a <see cref="BitmapImage"/>, optionally cropped to a normalized
    /// native-space region and/or rotated clockwise. Must be called on the UI thread.
    /// </summary>
    public static async Task<BitmapImage> RenderPageAsync(
        string filePath, int pageIndex, uint width = 200,
        CropRegion? crop = null, int rotationDegrees = 0)
    {
        // Render larger when cropping so the cropped region keeps its resolution.
        double widthScale = 1.0;
        if (crop is not null && !crop.IsFullPage)
        {
            double cropWidth = crop.Right - crop.Left;
            if (cropWidth > 0) widthScale = Math.Clamp(1.0 / cropWidth, 1.0, 6.0);
        }

        using InMemoryRandomAccessStream renderStream =
            await RenderToStreamAsync(filePath, pageIndex, (uint)(width * widthScale));

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(renderStream);
        BitmapBounds? bounds = ComputeBounds(crop, decoder.PixelWidth, decoder.PixelHeight);
        BitmapRotation rotation = ToBitmapRotation(rotationDegrees);

        BitmapImage bitmap = new();
        if (bounds is null && rotation == BitmapRotation.None)
        {
            renderStream.Seek(0);
            await bitmap.SetSourceAsync(renderStream);
            return bitmap;
        }

        using InMemoryRandomAccessStream outStream =
            await EncodeTransformedAsync(decoder, bounds, rotation);
        await bitmap.SetSourceAsync(outStream);
        return bitmap;
    }

    /// <summary>
    /// Rasterizes the cropped region of a page to PNG bytes for embedding into a saved PDF.
    /// Returns the bytes plus the cropped page's physical size in points (1/72 inch). The
    /// geometry honors the page's intrinsic <c>/Rotate</c>; the user rotation is applied
    /// separately via the PDF page's <c>/Rotate</c> entry.
    /// </summary>
    public static async Task<(byte[] Png, double WidthPt, double HeightPt)> RenderCroppedPageToPngAsync(
        string filePath, int pageIndex, CropRegion crop, double dpi = RasterizeDpi)
    {
        double displayedWidthPt;
        double displayedHeightPt;
        using (PdfSharp.Pdf.PdfDocument sharpDoc =
               PdfSharp.Pdf.IO.PdfReader.Open(filePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            PdfSharp.Pdf.PdfPage sharpPage = sharpDoc.Pages[pageIndex];
            int intrinsic = ((sharpPage.Rotate % 360) + 360) % 360;
            bool swap = intrinsic is 90 or 270;
            displayedWidthPt = swap ? sharpPage.Height.Point : sharpPage.Width.Point;
            displayedHeightPt = swap ? sharpPage.Width.Point : sharpPage.Height.Point;
        }

        double croppedWidthPt = Math.Max(1.0, (crop.Right - crop.Left) * displayedWidthPt);
        double croppedHeightPt = Math.Max(1.0, (crop.Bottom - crop.Top) * displayedHeightPt);

        uint fullRenderWidthPx = (uint)Math.Max(1.0, Math.Round(displayedWidthPt / 72.0 * dpi));

        using InMemoryRandomAccessStream renderStream =
            await RenderToStreamAsync(filePath, pageIndex, fullRenderWidthPx);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(renderStream);
        BitmapBounds? bounds = ComputeBounds(crop, decoder.PixelWidth, decoder.PixelHeight);

        using InMemoryRandomAccessStream outStream =
            await EncodeTransformedAsync(decoder, bounds, BitmapRotation.None);

        byte[] bytes = new byte[outStream.Size];
        using (DataReader reader = new(outStream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)outStream.Size);
            reader.ReadBytes(bytes);
        }

        return (bytes, croppedWidthPt, croppedHeightPt);
    }

    private static async Task<InMemoryRandomAccessStream> RenderToStreamAsync(
        string filePath, int pageIndex, uint width)
    {
        // Use a System.IO stream to avoid StorageFile access restrictions in unpackaged apps.
        using FileStream fileStream = File.OpenRead(filePath);
        using IRandomAccessStream winrtStream = fileStream.AsRandomAccessStream();
        PdfDocument pdfDoc = await PdfDocument.LoadFromStreamAsync(winrtStream);
        using PdfPage page = pdfDoc.GetPage((uint)pageIndex);

        InMemoryRandomAccessStream renderStream = new();
        PdfPageRenderOptions options = new() { DestinationWidth = Math.Max(1u, width) };
        await page.RenderToStreamAsync(renderStream, options);
        renderStream.Seek(0);
        return renderStream;
    }

    private static async Task<InMemoryRandomAccessStream> EncodeTransformedAsync(
        BitmapDecoder decoder, BitmapBounds? bounds, BitmapRotation rotation)
    {
        BitmapTransform transform = new() { Rotation = rotation };
        if (bounds.HasValue) transform.Bounds = bounds.Value;

        PixelDataProvider provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] pixels = provider.DetachPixelData();

        uint baseWidth = bounds?.Width ?? decoder.PixelWidth;
        uint baseHeight = bounds?.Height ?? decoder.PixelHeight;
        bool swap = rotation is BitmapRotation.Clockwise90Degrees or BitmapRotation.Clockwise270Degrees;
        uint outWidth = swap ? baseHeight : baseWidth;
        uint outHeight = swap ? baseWidth : baseHeight;

        InMemoryRandomAccessStream outStream = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            outWidth, outHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        outStream.Seek(0);
        return outStream;
    }

    private static BitmapBounds? ComputeBounds(CropRegion? crop, uint pixelWidth, uint pixelHeight)
    {
        if (crop is null || crop.IsFullPage) return null;

        uint x = (uint)Math.Clamp(Math.Round(crop.Left * pixelWidth), 0, pixelWidth - 1);
        uint y = (uint)Math.Clamp(Math.Round(crop.Top * pixelHeight), 0, pixelHeight - 1);
        uint w = (uint)Math.Clamp(Math.Round((crop.Right - crop.Left) * pixelWidth), 1, pixelWidth - x);
        uint h = (uint)Math.Clamp(Math.Round((crop.Bottom - crop.Top) * pixelHeight), 1, pixelHeight - y);
        return new BitmapBounds { X = x, Y = y, Width = w, Height = h };
    }

    private static BitmapRotation ToBitmapRotation(int degrees)
    {
        int normalized = ((degrees % 360) + 360) % 360;
        return normalized switch
        {
            90 => BitmapRotation.Clockwise90Degrees,
            180 => BitmapRotation.Clockwise180Degrees,
            270 => BitmapRotation.Clockwise270Degrees,
            _ => BitmapRotation.None,
        };
    }
}
