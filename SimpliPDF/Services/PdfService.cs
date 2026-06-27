using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SimpliPDF.Models;

namespace SimpliPDF.Services;

public class PdfService
{
    public int GetPageCount(string filePath)
    {
        using PdfDocument doc = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    public void MergeAndSave(IList<PreparedPage> pages, string outputPath)
    {
        using PdfDocument output = new();
        Dictionary<string, PdfDocument> sourceCache = new();
        List<IDisposable> resources = new();

        try
        {
            foreach (PreparedPage page in pages)
                AddPage(output, page, sourceCache, resources);

            output.Save(outputPath);
        }
        finally
        {
            foreach (IDisposable resource in resources)
                resource.Dispose();
            foreach (PdfDocument doc in sourceCache.Values)
                doc.Dispose();
        }
    }

    public void Split(IList<PreparedPage> pages, string outputFolder)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            using PdfDocument output = new();
            Dictionary<string, PdfDocument> sourceCache = new();
            List<IDisposable> resources = new();

            try
            {
                AddPage(output, pages[i], sourceCache, resources);
                output.Save(Path.Combine(outputFolder, $"Page_{i + 1}.pdf"));
            }
            finally
            {
                foreach (IDisposable resource in resources)
                    resource.Dispose();
                foreach (PdfDocument doc in sourceCache.Values)
                    doc.Dispose();
            }
        }
    }

    private static void AddPage(PdfDocument output, PreparedPage page,
        Dictionary<string, PdfDocument> sourceCache, List<IDisposable> resources)
    {
        if (page is RasterizedPage raster)
        {
            AddRasterizedPage(output, raster, resources);
            return;
        }

        ImportedPage imported = (ImportedPage)page;
        if (!sourceCache.TryGetValue(imported.SourceFilePath, out PdfDocument? sourceDoc))
        {
            sourceDoc = PdfReader.Open(imported.SourceFilePath, PdfDocumentOpenMode.Import);
            sourceCache[imported.SourceFilePath] = sourceDoc;
        }

        PdfPage importedPage = output.AddPage(sourceDoc.Pages[imported.OriginalPageIndex]);
        if (imported.Rotation != 0)
            importedPage.Rotate = (importedPage.Rotate + imported.Rotation) % 360;
    }

    private static void AddRasterizedPage(PdfDocument output, RasterizedPage raster,
        List<IDisposable> resources)
    {
        PdfPage page = output.AddPage();
        page.Width = XUnit.FromPoint(raster.WidthPt);
        page.Height = XUnit.FromPoint(raster.HeightPt);

        // The XImage and its backing stream must stay alive until the document
        // is saved, so they are disposed by the caller after Save().
        MemoryStream imageStream = new(raster.Png);
        XImage image = XImage.FromStream(imageStream);
        resources.Add(image);
        resources.Add(imageStream);

        using (XGraphics gfx = XGraphics.FromPdfPage(page))
            gfx.DrawImage(image, 0, 0, raster.WidthPt, raster.HeightPt);

        if (raster.Rotation != 0)
            page.Rotate = ((raster.Rotation % 360) + 360) % 360;
    }
}
