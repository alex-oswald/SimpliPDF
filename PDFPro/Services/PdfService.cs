using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFPro.Models;

namespace PDFPro.Services;

public class PdfService
{
    public int GetPageCount(string filePath)
    {
        using var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    public void MergeAndSave(IList<PdfPageItem> pages, string outputPath)
    {
        using var output = new PdfDocument();
        var sourceCache = new Dictionary<string, PdfDocument>();

        try
        {
            foreach (var page in pages)
            {
                if (!sourceCache.TryGetValue(page.SourceFilePath, out var sourceDoc))
                {
                    sourceDoc = PdfReader.Open(page.SourceFilePath, PdfDocumentOpenMode.Import);
                    sourceCache[page.SourceFilePath] = sourceDoc;
                }

                var importedPage = output.AddPage(sourceDoc.Pages[page.OriginalPageIndex]);
                if (page.Rotation != 0)
                    importedPage.Rotate = (importedPage.Rotate + page.Rotation) % 360;
            }

            output.Save(outputPath);
        }
        finally
        {
            foreach (var doc in sourceCache.Values)
                doc.Dispose();
        }
    }

    public void Split(IList<PdfPageItem> pages, string outputFolder)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            using var output = new PdfDocument();
            var page = pages[i];

            using var sourceDoc = PdfReader.Open(page.SourceFilePath, PdfDocumentOpenMode.Import);
            var importedPage = output.AddPage(sourceDoc.Pages[page.OriginalPageIndex]);
            if (page.Rotation != 0)
                importedPage.Rotate = (importedPage.Rotate + page.Rotation) % 360;

            output.Save(Path.Combine(outputFolder, $"Page_{i + 1}.pdf"));
        }
    }
}
