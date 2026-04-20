using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpliPDF.Services;
using Windows.Foundation;

namespace SimpliPDF.Dialogs;

public sealed partial class ScanDialog : ContentDialog
{
    public ScannerInfo? SelectedScanner { get; private set; }
    public int SelectedDpi { get; private set; } = 300;
    public ScanColorMode SelectedColorMode { get; private set; } = ScanColorMode.Color;
    public bool NoScannersFound { get; private set; }

    /// <summary>Path to the scanned PDF, set after a successful scan.</summary>
    public string? ScannedPdfPath { get; private set; }

    private bool _isDragging;
    private Point _dragStart;
    private ScanCropRegion? _cropRegion;
    private CancellationTokenSource? _cancelCts;

    public ScanDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadScannersAsync();
    }

    private async Task LoadScannersAsync()
    {
        var scanners = await ScanService.GetScannersAsync();

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
            var caps = await ScanService.GetCapabilitiesAsync(SelectedScanner.DeviceId);

            // Populate DPI dropdown
            DpiCombo.Items.Clear();
            int defaultDpiIndex = 0;
            for (int i = 0; i < caps.SupportedDpi.Count; i++)
            {
                var dpi = caps.SupportedDpi[i];
                DpiCombo.Items.Add(new ComboBoxItem { Content = $"{dpi} DPI", Tag = dpi.ToString() });
                if (dpi == 300) defaultDpiIndex = i;
            }
            DpiCombo.SelectedIndex = defaultDpiIndex;
            DpiCombo.IsEnabled = true;

            // Populate color mode dropdown
            ColorModeCombo.Items.Clear();
            foreach (var mode in caps.SupportedColorModes)
            {
                var label = mode switch
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
        var token = _cancelCts.Token;
        SetBusy(true, "Previewing...");
        try
        {
            var imagePath = await ScanService.PreviewAsync(SelectedScanner.DeviceId);

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

            var bitmap = new BitmapImage();
            using var stream = File.OpenRead(imagePath);
            using var winrtStream = stream.AsRandomAccessStream();
            await bitmap.SetSourceAsync(winrtStream);

            PreviewImage.Source = bitmap;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;

            // Enable crop overlay and reset any previous crop
            CropCanvas.Visibility = Visibility.Visible;
            ClearCrop();

            StatusText.Text = "Preview ready – drag to crop, or scan full page";

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
        var deferral = args.GetDeferral();

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
            var token = _cancelCts.Token;
            SetBusy(true, "Scanning...");

            var pdfPath = await ScanService.ScanAsync(
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
        StatusText.Text = "Cancelling...";
        CancelOperationButton.IsEnabled = false;
    }

    // ── Crop interaction ──────────────────────────────────────────

    private void OnCropPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CropCanvas.CapturePointer(e.Pointer);
        _dragStart = e.GetCurrentPoint(CropCanvas).Position;
        _isDragging = true;

        CropRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(CropRect, _dragStart.X);
        Canvas.SetTop(CropRect, _dragStart.Y);
        CropRect.Width = 0;
        CropRect.Height = 0;
    }

    private void OnCropPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;

        var pos = e.GetCurrentPoint(CropCanvas).Position;
        var canvasW = CropCanvas.ActualWidth;
        var canvasH = CropCanvas.ActualHeight;

        double x = Math.Clamp(Math.Min(_dragStart.X, pos.X), 0, canvasW);
        double y = Math.Clamp(Math.Min(_dragStart.Y, pos.Y), 0, canvasH);
        double w = Math.Clamp(Math.Abs(pos.X - _dragStart.X), 0, canvasW - x);
        double h = Math.Clamp(Math.Abs(pos.Y - _dragStart.Y), 0, canvasH - y);

        Canvas.SetLeft(CropRect, x);
        Canvas.SetTop(CropRect, y);
        CropRect.Width = w;
        CropRect.Height = h;

        UpdateDimOverlays(x, y, w, h, canvasW, canvasH);
    }

    private void OnCropPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CropCanvas.ReleasePointerCapture(e.Pointer);
        _isDragging = false;

        if (CropRect.Width < 5 || CropRect.Height < 5)
        {
            ClearCrop();
            return;
        }

        // Convert canvas-relative crop rectangle to 0–1 fractions of the image
        var imageBounds = GetImageBounds();
        if (imageBounds.Width <= 0 || imageBounds.Height <= 0)
        {
            ClearCrop();
            return;
        }

        double left = Math.Clamp((Canvas.GetLeft(CropRect) - imageBounds.X) / imageBounds.Width, 0, 1);
        double top = Math.Clamp((Canvas.GetTop(CropRect) - imageBounds.Y) / imageBounds.Height, 0, 1);
        double right = Math.Clamp(left + CropRect.Width / imageBounds.Width, 0, 1);
        double bottom = Math.Clamp(top + CropRect.Height / imageBounds.Height, 0, 1);

        if (right - left < 0.01 || bottom - top < 0.01)
        {
            ClearCrop();
            return;
        }

        _cropRegion = new ScanCropRegion(left, top, right, bottom);
        ResetCropButton.Visibility = Visibility.Visible;
        StatusText.Text = "Crop selected – scan will capture the highlighted area";
    }

    private void OnResetCropClick(object sender, RoutedEventArgs e)
    {
        ClearCrop();
        StatusText.Text = "Crop cleared – scan will capture full page";
    }

    private void ClearCrop()
    {
        _cropRegion = null;
        _isDragging = false;
        CropRect.Visibility = Visibility.Collapsed;
        ResetCropButton.Visibility = Visibility.Collapsed;

        // Hide dim overlays
        DimTop.Width = DimTop.Height = 0;
        DimBottom.Width = DimBottom.Height = 0;
        DimLeft.Width = DimLeft.Height = 0;
        DimRight.Width = DimRight.Height = 0;
    }

    private void UpdateDimOverlays(double x, double y, double w, double h, double canvasW, double canvasH)
    {
        // Top strip (full width, above crop)
        Canvas.SetLeft(DimTop, 0);
        Canvas.SetTop(DimTop, 0);
        DimTop.Width = canvasW;
        DimTop.Height = y;

        // Bottom strip (full width, below crop)
        Canvas.SetLeft(DimBottom, 0);
        Canvas.SetTop(DimBottom, y + h);
        DimBottom.Width = canvasW;
        DimBottom.Height = Math.Max(0, canvasH - y - h);

        // Left strip (between top and bottom, left of crop)
        Canvas.SetLeft(DimLeft, 0);
        Canvas.SetTop(DimLeft, y);
        DimLeft.Width = x;
        DimLeft.Height = h;

        // Right strip (between top and bottom, right of crop)
        Canvas.SetLeft(DimRight, x + w);
        Canvas.SetTop(DimRight, y);
        DimRight.Width = Math.Max(0, canvasW - x - w);
        DimRight.Height = h;
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
