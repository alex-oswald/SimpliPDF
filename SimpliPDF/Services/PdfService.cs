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

    public void MergeAndSave(IList<PdfPageItem> pages, string outputPath)
    {
        using PdfDocument output = new PdfDocument();
        Dictionary<string, PdfDocument> sourceCache = new Dictionary<string, PdfDocument>();

        try
        {
            foreach (PdfPageItem page in pages)
            {
                if (!sourceCache.TryGetValue(page.SourceFilePath, out PdfDocument? sourceDoc))
                {
                    sourceDoc = PdfReader.Open(page.SourceFilePath, PdfDocumentOpenMode.Import);
                    sourceCache[page.SourceFilePath] = sourceDoc;
                }

                PdfPage importedPage = output.AddPage(sourceDoc.Pages[page.OriginalPageIndex]);
                if (page.Rotation != 0)
                    importedPage.Rotate = (importedPage.Rotate + page.Rotation) % 360;
            }

            output.Save(outputPath);
        }
        finally
        {
            foreach (PdfDocument doc in sourceCache.Values)
                doc.Dispose();
        }
    }

    public void Split(IList<PdfPageItem> pages, string outputFolder)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            using PdfDocument output = new PdfDocument();
            PdfPageItem page = pages[i];

            using PdfDocument sourceDoc = PdfReader.Open(page.SourceFilePath, PdfDocumentOpenMode.Import);
            PdfPage importedPage = output.AddPage(sourceDoc.Pages[page.OriginalPageIndex]);
            if (page.Rotation != 0)
                importedPage.Rotate = (importedPage.Rotate + page.Rotation) % 360;

            output.Save(Path.Combine(outputFolder, $"Page_{i + 1}.pdf"));
        }
    }
}
