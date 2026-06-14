using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpliPDF.Services;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace SimpliPDF.Dialogs;

public sealed partial class ScanDialog : ContentDialog
{
    public ScannerInfo? SelectedScanner { get; private set; }
    public int SelectedDpi { get; private set; } = 300;
    public ScanColorMode SelectedColorMode { get; private set; } = ScanColorMode.Color;
    public bool NoScannersFound { get; private set; }

    /// <summary>Path to the scanned PDF, set after a successful scan.</summary>
    public string? ScannedPdfPath { get; private set; }

    private Point _dragStart;
    private ScanCropRegion? _cropRegion;
    private CancellationTokenSource? _cancelCts;

    [Flags]
    private enum DragEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }
    private enum DragMode { None, Resize, Create }

    private DragMode _dragMode;
    private DragEdge _dragEdges;
    private const double HandleMargin = 10;
    private const double MinCropSize = 20;

    public ScanDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadScannersAsync();
    }

    private async Task LoadScannersAsync()
    {
        List<ScannerInfo> scanners = await ScanService.GetScannersAsync();

        if (scanners.Count == 0)
        {
            NoScannersFound = true;
            Hide();
            return;
        }

        ScannerCombo.ItemsSource = scanners;
        ScannerCombo.SelectedIndex = 0;
    }

    private async void OnScannerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedScanner = ScannerCombo.SelectedItem as ScannerInfo;
        if (SelectedScanner == null)
        {
            IsPrimaryButtonEnabled = false;
            PreviewButton.IsEnabled = false;
            return;
        }

        // Query capabilities from the selected scanner
        DpiCombo.IsEnabled = false;
        ColorModeCombo.IsEnabled = false;
        StatusText.Text = "Reading scanner capabilities...";

        try
        {
            ScannerCapabilities caps = await ScanService.GetCapabilitiesAsync(SelectedScanner.DeviceId);

            // Populate DPI dropdown
            DpiCombo.Items.Clear();
            int defaultDpiIndex = 0;
            for (int i = 0; i < caps.SupportedDpi.Count; i++)
            {
                int dpi = caps.SupportedDpi[i];
                DpiCombo.Items.Add(new ComboBoxItem { Content = $"{dpi} DPI", Tag = dpi.ToString() });
                if (dpi == 300) defaultDpiIndex = i;
            }
            DpiCombo.SelectedIndex = defaultDpiIndex;
            DpiCombo.IsEnabled = true;

            // Populate color mode dropdown
            ColorModeCombo.Items.Clear();
            foreach (ScanColorMode mode in caps.SupportedColorModes)
            {
                string label = mode switch
                {
                    ScanColorMode.Color => "Color",
                    ScanColorMode.Grayscale => "Grayscale",
                    ScanColorMode.BlackAndWhite => "Black & White",
                    _ => mode.ToString()
                };
                ColorModeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = ((int)mode).ToString() });
            }
            ColorModeCombo.SelectedIndex = 0;
            ColorModeCombo.IsEnabled = true;

            IsPrimaryButtonEnabled = true;
            PreviewButton.IsEnabled = true;
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to read capabilities: {ex.Message}";
            // Fall back to enabling with defaults
            IsPrimaryButtonEnabled = true;
            PreviewButton.IsEnabled = true;
        }
    }

    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (SelectedScanner == null) return;

        _cancelCts = new CancellationTokenSource();
        CancellationToken token = _cancelCts.Token;
        SetBusy(true, "Previewing...");
        try
        {
            string? imagePath = await ScanService.PreviewAsync(SelectedScanner.DeviceId);

            if (token.IsCancellationRequested)
            {
                if (imagePath != null) try { File.Delete(imagePath); } catch { }
                StatusText.Text = "Preview cancelled";
                return;
            }

            if (imagePath == null)
            {
                StatusText.Text = "Preview failed";
                return;
            }

            BitmapImage bitmap = new BitmapImage();
            using FileStream stream = File.OpenRead(imagePath);
            using IRandomAccessStream winrtStream = stream.AsRandomAccessStream();
            await bitmap.SetSourceAsync(winrtStream);

            PreviewImage.Source = bitmap;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;

            // Enable crop overlay and initialize to full image
            CropCanvas.Visibility = Visibility.Visible;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                InitializeCropToFullImage);

            StatusText.Text = "Preview ready – drag edges to crop, or scan full page";

            try { File.Delete(imagePath); } catch { }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                StatusText.Text = $"Preview failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Defer closing so we can scan while the dialog stays open
        ContentDialogButtonClickDeferral deferral = args.GetDeferral();

        try
        {
            if (DpiCombo.SelectedItem is ComboBoxItem dpiItem && dpiItem.Tag is string dpiStr)
                SelectedDpi = int.Parse(dpiStr);
            if (ColorModeCombo.SelectedItem is ComboBoxItem colorItem && colorItem.Tag is string colorStr)
                SelectedColorMode = (ScanColorMode)int.Parse(colorStr);

            if (SelectedScanner == null)
            {
                args.Cancel = true;
                return;
            }

            _cancelCts = new CancellationTokenSource();
            CancellationToken token = _cancelCts.Token;
            SetBusy(true, "Scanning...");

            string? pdfPath = await ScanService.ScanAsync(
                SelectedScanner.DeviceId, SelectedDpi, SelectedColorMode, _cropRegion);

            if (token.IsCancellationRequested)
            {
                if (pdfPath != null) try { File.Delete(pdfPath); } catch { }
                args.Cancel = true;
                StatusText.Text = "Scan cancelled";
                SetBusy(false);
                return;
            }

            if (pdfPath == null)
            {
                args.Cancel = true;
                StatusText.Text = "Scan cancelled";
                SetBusy(false);
                return;
            }

            ScannedPdfPath = pdfPath;
            StatusText.Text = "Scan complete";
            // Deferral completes → dialog closes with Primary result
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            StatusText.Text = $"Scan failed: {ex.Message}";
            SetBusy(false);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        ScanProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.IsEnabled = busy;
        PreviewButton.IsEnabled = !busy && SelectedScanner != null;
        IsPrimaryButtonEnabled = !busy && SelectedScanner != null;
        CloseButtonText = busy ? "" : "Cancel";
        ScannerCombo.IsEnabled = !busy;
        DpiCombo.IsEnabled = !busy && DpiCombo.Items.Count > 0;
        ColorModeCombo.IsEnabled = !busy && ColorModeCombo.Items.Count > 0;

        if (status != null) StatusText.Text = status;
        else if (!busy) StatusText.Text = "";
    }

    private void OnCancelOperationClick(object sender, RoutedEventArgs e)
    {
        _cancelCts?.Cancel();
        SetBusy(false, "Cancelled");
    }

    // ── Crop interaction ──────────────────────────────────────────

    private void InitializeCropToFullImage()
    {
        Rect bounds = GetImageBounds();
        Canvas.SetLeft(CropRect, bounds.X);
        Canvas.SetTop(CropRect, bounds.Y);
        CropRect.Width = bounds.Width;
        CropRect.Height = bounds.Height;
        CropRect.Visibility = Visibility.Visible;

        _cropRegion = null;
        ResetCropButton.Visibility = Visibility.Collapsed;
        UpdateCropVisuals();
    }

    private DragEdge HitTestEdges(Point pos)
    {
        if (CropRect.Visibility == Visibility.Collapsed) return DragEdge.None;

        double cx = Canvas.GetLeft(CropRect);
        double cy = Canvas.GetTop(CropRect);
        double cr = cx + CropRect.Width;
        double cb = cy + CropRect.Height;

        // Must be within extended rect area
        if (pos.X < cx - HandleMargin || pos.X > cr + HandleMargin ||
            pos.Y < cy - HandleMargin || pos.Y > cb + HandleMargin)
            return DragEdge.None;

        DragEdge edges = DragEdge.None;
        if (Math.Abs(pos.X - cx) <= HandleMargin) edges |= DragEdge.Left;
        else if (Math.Abs(pos.X - cr) <= HandleMargin) edges |= DragEdge.Right;

        if (Math.Abs(pos.Y - cy) <= HandleMargin) edges |= DragEdge.Top;
        else if (Math.Abs(pos.Y - cb) <= HandleMargin) edges |= DragEdge.Bottom;

        return edges;
    }

    private void OnCropPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Point pos = e.GetCurrentPoint(CropCanvas).Position;
        CropCanvas.CapturePointer(e.Pointer);

        _dragEdges = HitTestEdges(pos);
        if (_dragEdges != DragEdge.None)
        {
            _dragMode = DragMode.Resize;
        }
        else
        {
            _dragMode = DragMode.Create;
            _dragStart = pos;
            Canvas.SetLeft(CropRect, pos.X);
            Canvas.SetTop(CropRect, pos.Y);
            CropRect.Width = 0;
            CropRect.Height = 0;
            CropRect.Visibility = Visibility.Visible;
        }
    }

    private void OnCropPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None) return;

        Point pos = e.GetCurrentPoint(CropCanvas).Position;
        double canvasW = CropCanvas.ActualWidth;
        double canvasH = CropCanvas.ActualHeight;
        pos = new Point(Math.Clamp(pos.X, 0, canvasW), Math.Clamp(pos.Y, 0, canvasH));

        if (_dragMode == DragMode.Create)
        {
            double x = Math.Min(_dragStart.X, pos.X);
            double y = Math.Min(_dragStart.Y, pos.Y);
            double w = Math.Abs(pos.X - _dragStart.X);
            double h = Math.Abs(pos.Y - _dragStart.Y);

            Canvas.SetLeft(CropRect, x);
            Canvas.SetTop(CropRect, y);
            CropRect.Width = w;
            CropRect.Height = h;
        }
        else
        {
            double left = Canvas.GetLeft(CropRect);
            double top = Canvas.GetTop(CropRect);
            double right = left + CropRect.Width;
            double bottom = top + CropRect.Height;

            if (_dragEdges.HasFlag(DragEdge.Left)) left = Math.Min(pos.X, right - MinCropSize);
            if (_dragEdges.HasFlag(DragEdge.Right)) right = Math.Max(pos.X, left + MinCropSize);
            if (_dragEdges.HasFlag(DragEdge.Top)) top = Math.Min(pos.Y, bottom - MinCropSize);
            if (_dragEdges.HasFlag(DragEdge.Bottom)) bottom = Math.Max(pos.Y, top + MinCropSize);

            Canvas.SetLeft(CropRect, Math.Max(0, left));
            Canvas.SetTop(CropRect, Math.Max(0, top));
            CropRect.Width = Math.Min(right - Math.Max(0, left), canvasW - Math.Max(0, left));
            CropRect.Height = Math.Min(bottom - Math.Max(0, top), canvasH - Math.Max(0, top));
        }

        UpdateCropVisuals();
    }

    private void OnCropPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CropCanvas.ReleasePointerCapture(e.Pointer);
        if (_dragMode == DragMode.None) return;
        _dragMode = DragMode.None;

        if (CropRect.Width < MinCropSize || CropRect.Height < MinCropSize)
        {
            InitializeCropToFullImage();
            return;
        }

        ComputeCropRegion();
    }

    private void ComputeCropRegion()
    {
        Rect imageBounds = GetImageBounds();
        if (imageBounds.Width <= 0 || imageBounds.Height <= 0) return;

        double left = Math.Clamp((Canvas.GetLeft(CropRect) - imageBounds.X) / imageBounds.Width, 0, 1);
        double top = Math.Clamp((Canvas.GetTop(CropRect) - imageBounds.Y) / imageBounds.Height, 0, 1);
        double right = Math.Clamp(left + CropRect.Width / imageBounds.Width, 0, 1);
        double bottom = Math.Clamp(top + CropRect.Height / imageBounds.Height, 0, 1);

        // If essentially full image, treat as no crop
        if (left < 0.02 && top < 0.02 && right > 0.98 && bottom > 0.98)
        {
            _cropRegion = null;
            ResetCropButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "Preview ready – drag edges to crop, or scan full page";
        }
        else
        {
            _cropRegion = new ScanCropRegion(left, top, right, bottom);
            ResetCropButton.Visibility = Visibility.Visible;
            StatusText.Text = "Crop selected – scan will capture the highlighted area";
        }
    }

    private void OnResetCropClick(object sender, RoutedEventArgs e)
    {
        InitializeCropToFullImage();
        StatusText.Text = "Crop reset – scan will capture full page";
    }

    private void ClearCrop()
    {
        _cropRegion = null;
        _dragMode = DragMode.None;
        CropRect.Visibility = Visibility.Collapsed;
        ResetCropButton.Visibility = Visibility.Collapsed;
        SetHandlesVisibility(Visibility.Collapsed);

        DimTop.Width = DimTop.Height = 0;
        DimBottom.Width = DimBottom.Height = 0;
        DimLeft.Width = DimLeft.Height = 0;
        DimRight.Width = DimRight.Height = 0;
    }

    private void UpdateCropVisuals()
    {
        double x = Canvas.GetLeft(CropRect);
        double y = Canvas.GetTop(CropRect);
        double w = CropRect.Width;
        double h = CropRect.Height;
        double canvasW = CropCanvas.ActualWidth;
        double canvasH = CropCanvas.ActualHeight;

        UpdateDimOverlays(x, y, w, h, canvasW, canvasH);
        UpdateHandlePositions(x, y, w, h);
        SetHandlesVisibility(Visibility.Visible);
    }

    private void UpdateDimOverlays(double x, double y, double w, double h, double canvasW, double canvasH)
    {
        Canvas.SetLeft(DimTop, 0);
        Canvas.SetTop(DimTop, 0);
        DimTop.Width = canvasW;
        DimTop.Height = y;

        Canvas.SetLeft(DimBottom, 0);
        Canvas.SetTop(DimBottom, y + h);
        DimBottom.Width = canvasW;
        DimBottom.Height = Math.Max(0, canvasH - y - h);

        Canvas.SetLeft(DimLeft, 0);
        Canvas.SetTop(DimLeft, y);
        DimLeft.Width = x;
        DimLeft.Height = h;

        Canvas.SetLeft(DimRight, x + w);
        Canvas.SetTop(DimRight, y);
        DimRight.Width = Math.Max(0, canvasW - x - w);
        DimRight.Height = h;
    }

    private void UpdateHandlePositions(double x, double y, double w, double h)
    {
        const double hs = 10;
        const double hh = hs / 2;

        PositionHandle(HandleTL, x - hh, y - hh);
        PositionHandle(HandleT, x + w / 2 - hh, y - hh);
        PositionHandle(HandleTR, x + w - hh, y - hh);
        PositionHandle(HandleL, x - hh, y + h / 2 - hh);
        PositionHandle(HandleR, x + w - hh, y + h / 2 - hh);
        PositionHandle(HandleBL, x - hh, y + h - hh);
        PositionHandle(HandleB, x + w / 2 - hh, y + h - hh);
        PositionHandle(HandleBR, x + w - hh, y + h - hh);
    }

    private static void PositionHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
    }

    private void SetHandlesVisibility(Visibility v)
    {
        HandleTL.Visibility = v;
        HandleT.Visibility = v;
        HandleTR.Visibility = v;
        HandleL.Visibility = v;
        HandleR.Visibility = v;
        HandleBL.Visibility = v;
        HandleB.Visibility = v;
        HandleBR.Visibility = v;
    }

    /// <summary>
    /// Compute the rendered bounds of the preview image within the canvas.
    /// The Image uses Stretch="Uniform", so it is letterboxed inside its container.
    /// </summary>
    private Rect GetImageBounds()
    {
        if (PreviewImage.Source is not BitmapImage bmp || bmp.PixelWidth == 0 || bmp.PixelHeight == 0)
            return new Rect(0, 0, CropCanvas.ActualWidth, CropCanvas.ActualHeight);

        double canvasW = CropCanvas.ActualWidth;
        double canvasH = CropCanvas.ActualHeight;
        double imageAspect = (double)bmp.PixelWidth / bmp.PixelHeight;
        double canvasAspect = canvasW / canvasH;

        double renderW, renderH;
        if (imageAspect > canvasAspect)
        {
            renderW = canvasW;
            renderH = canvasW / imageAspect;
        }
        else
        {
            renderH = canvasH;
            renderW = canvasH * imageAspect;
        }

        double offsetX = (canvasW - renderW) / 2;
        double offsetY = (canvasH - renderH) / 2;

        return new Rect(offsetX, offsetY, renderW, renderH);
    }
}
