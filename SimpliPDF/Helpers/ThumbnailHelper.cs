using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace SimpliPDF.Helpers;

public static class ThumbnailHelper
{
    public static async Task<BitmapImage> RenderPageAsync(string filePath, int pageIndex, uint width = 200)
    {
        // Use System.IO stream to avoid StorageFile access restrictions in unpackaged apps
        using var fileStream = File.OpenRead(filePath);
        using var winrtStream = fileStream.AsRandomAccessStream();
        var pdfDoc = await PdfDocument.LoadFromStreamAsync(winrtStream);
        using var page = pdfDoc.GetPage((uint)pageIndex);

        using var renderStream = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions { DestinationWidth = width };
        await page.RenderToStreamAsync(renderStream, options);
        renderStream.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(renderStream);
        return bitmap;
    }
}
