using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace SimpliPDF.Helpers;

public static class ThumbnailHelper
{
    public static async Task<BitmapImage> RenderPageAsync(string filePath, int pageIndex, uint width = 200)
    {
        // Use System.IO stream to avoid StorageFile access restrictions in unpackaged apps
        using FileStream fileStream = File.OpenRead(filePath);
        using IRandomAccessStream winrtStream = fileStream.AsRandomAccessStream();
        PdfDocument pdfDoc = await PdfDocument.LoadFromStreamAsync(winrtStream);
        using PdfPage page = pdfDoc.GetPage((uint)pageIndex);

        using InMemoryRandomAccessStream renderStream = new InMemoryRandomAccessStream();
        PdfPageRenderOptions options = new PdfPageRenderOptions { DestinationWidth = width };
        await page.RenderToStreamAsync(renderStream, options);
        renderStream.Seek(0);

        BitmapImage bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(renderStream);
        return bitmap;
    }
}
