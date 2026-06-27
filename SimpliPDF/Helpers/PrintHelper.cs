using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using SimpliPDF.Models;
using Windows.Graphics.Printing;

namespace SimpliPDF.Helpers;

public class PrintHelper
{
    private readonly IntPtr _hwnd;
    private PrintManager? _printManager;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printDocumentSource;
    private IList<PdfPageItem>? _pagesToPrint;
    private readonly List<UIElement> _renderedPages = [];

    public PrintHelper(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    public async Task PrintAsync(IList<PdfPageItem> pages)
    {
        _pagesToPrint = pages;
        _renderedPages.Clear();

        _printManager = PrintManagerInterop.GetForWindow(_hwnd);
        _printManager.PrintTaskRequested += OnPrintTaskRequested;

        _printDocument = new PrintDocument();
        _printDocumentSource = _printDocument.DocumentSource;
        _printDocument.Paginate += OnPaginate;
        _printDocument.GetPreviewPage += OnGetPreviewPage;
        _printDocument.AddPages += OnAddPages;

        try
        {
            await PrintManagerInterop.ShowPrintUIForWindowAsync(_hwnd);
        }
        finally
        {
            _printManager.PrintTaskRequested -= OnPrintTaskRequested;
            _printDocument.Paginate -= OnPaginate;
            _printDocument.GetPreviewPage -= OnGetPreviewPage;
            _printDocument.AddPages -= OnAddPages;
        }
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        PrintTask printTask = args.Request.CreatePrintTask("SimpliPDF Document", sourceArgs =>
        {
            sourceArgs.SetSource(_printDocumentSource);
        });
    }

    private void OnPaginate(object sender, PaginateEventArgs e)
    {
        _renderedPages.Clear();

        if (_pagesToPrint == null) return;

        foreach (PdfPageItem page in _pagesToPrint)
        {
            Image image = new Image
            {
                Source = page.DisplayThumbnail,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                Width = 800,
                Height = 1100,
            };

            if (page.Rotation != 0)
            {
                image.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                image.RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform
                {
                    Angle = page.Rotation
                };
            }

            _renderedPages.Add(image);
        }

        _printDocument?.SetPreviewPageCount(_renderedPages.Count, PreviewPageCountType.Final);
    }

    private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (e.PageNumber > 0 && e.PageNumber <= _renderedPages.Count)
            _printDocument?.SetPreviewPage(e.PageNumber, _renderedPages[e.PageNumber - 1]);
    }

    private void OnAddPages(object sender, AddPagesEventArgs e)
    {
        foreach (UIElement page in _renderedPages)
            _printDocument?.AddPage(page);

        _printDocument?.AddPagesComplete();
    }
}
