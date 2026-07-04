using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using SimpliPDF.Interop;
using SimpliPDF.Models;
using Windows.Foundation;
using Windows.Graphics.Printing;

namespace SimpliPDF.Helpers;

public class PrintHelper
{
    // Fallback page size (8.5" x 11" at 96 DPI) used only if the printer reports no page description.
    private const double FallbackPageWidth = 816;
    private const double FallbackPageHeight = 1056;

    // Fraction of the page reserved as a margin on every edge.
    private const double PageMarginFraction = 0.04;

    private readonly IntPtr _hwnd;
    private readonly Canvas _printCanvas;
    private PrintManager? _printManager;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printDocumentSource;
    private IList<PdfPageItem>? _pagesToPrint;
    private readonly List<UIElement> _renderedPages = [];

    public PrintHelper(IntPtr hwnd, Canvas printCanvas)
    {
        _hwnd = hwnd;
        _printCanvas = printCanvas;
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
            // ShowPrintUIForWindowAsync is driven through PrintManagerInteropNative rather than the
            // CsWinRT projection: the projected overload returns IAsyncOperation<bool>, whose helper
            // type cannot be built under Native AOT (see PrintManagerInteropNative for the full story).
            await PrintManagerInteropNative.ShowPrintUIForWindowAsync(_hwnd);
        }
        finally
        {
            _printManager.PrintTaskRequested -= OnPrintTaskRequested;
            _printDocument.Paginate -= OnPaginate;
            _printDocument.GetPreviewPage -= OnGetPreviewPage;
            _printDocument.AddPages -= OnAddPages;

            // Release the page visuals we parked in the visual tree.
            _printCanvas.Children.Clear();
            _renderedPages.Clear();
        }
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        args.Request.CreatePrintTask("SimpliPDF Document", sourceArgs =>
        {
            sourceArgs.SetSource(_printDocumentSource);
        });
    }

    private void OnPaginate(object sender, PaginateEventArgs e)
    {
        _renderedPages.Clear();
        _printCanvas.Children.Clear();

        if (_pagesToPrint == null) return;

        double pageWidth = FallbackPageWidth;
        double pageHeight = FallbackPageHeight;

        // Size the printed pages to the selected printer's page so the preview fills the sheet.
        if (e.PrintTaskOptions is PrintTaskOptions options)
        {
            PrintPageDescription description = options.GetPageDescription(0);
            if (description.PageSize.Width > 0 && description.PageSize.Height > 0)
            {
                pageWidth = description.PageSize.Width;
                pageHeight = description.PageSize.Height;
            }
        }

        foreach (PdfPageItem page in _pagesToPrint)
        {
            UIElement pageVisual = BuildPageVisual(page, pageWidth, pageHeight);

            // The print system can only rasterize elements that are in the visual tree and have been
            // laid out. Parent each page in the off-screen canvas and force a layout pass before it
            // is ever handed to SetPreviewPage / AddPage — without this the preview hangs forever.
            _printCanvas.Children.Add(pageVisual);
            _renderedPages.Add(pageVisual);
        }

        _printCanvas.InvalidateMeasure();
        _printCanvas.UpdateLayout();

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

    /// <summary>
    /// Builds a single white, page-sized sheet with the page's thumbnail centered inside the printable
    /// margins. Rotation is applied as a centered render transform; for quarter turns the image's layout
    /// box is swapped first so the rotated result still fits the sheet instead of clipping.
    /// </summary>
    private static UIElement BuildPageVisual(PdfPageItem page, double pageWidth, double pageHeight)
    {
        Grid sheet = new Grid
        {
            Width = pageWidth,
            Height = pageHeight,
            Background = new SolidColorBrush(Colors.White),
        };

        double margin = Math.Min(pageWidth, pageHeight) * PageMarginFraction;
        double availableWidth = Math.Max(pageWidth - (margin * 2), 1);
        double availableHeight = Math.Max(pageHeight - (margin * 2), 1);

        int rotation = ((page.Rotation % 360) + 360) % 360;
        bool quarterTurned = rotation is 90 or 270;

        Image image = new Image
        {
            Source = page.DisplayThumbnail,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = quarterTurned ? availableHeight : availableWidth,
            Height = quarterTurned ? availableWidth : availableHeight,
        };

        if (rotation != 0)
        {
            image.RenderTransformOrigin = new Point(0.5, 0.5);
            image.RenderTransform = new RotateTransform { Angle = rotation };
        }

        sheet.Children.Add(image);
        return sheet;
    }
}
