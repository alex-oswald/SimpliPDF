using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpliPDF.Models;
using SimpliPDF.Services;
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

    private CancellationTokenSource? _cancelCts;

    public ScanDialog()
    {
        InitializeComponent();
        Crop.CropChanged += OnCropChanged;
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

            Crop.Source = bitmap;
            Crop.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;

            // Initialize crop overlay to the full image once layout has run.
            ResetCropButton.Visibility = Visibility.Collapsed;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                Crop.InitializeToFull);

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

            CropRegion? region = Crop.GetCropRegion();
            ScanCropRegion? scanCrop = region is null
                ? null
                : new ScanCropRegion(region.Left, region.Top, region.Right, region.Bottom);

            string? pdfPath = await ScanService.ScanAsync(
                SelectedScanner.DeviceId, SelectedDpi, SelectedColorMode, scanCrop);

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

    private void OnCropChanged(object? sender, EventArgs e)
    {
        if (Crop.GetCropRegion() is null)
        {
            ResetCropButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "Preview ready – drag edges to crop, or scan full page";
        }
        else
        {
            ResetCropButton.Visibility = Visibility.Visible;
            StatusText.Text = "Crop selected – scan will capture the highlighted area";
        }
    }

    private void OnResetCropClick(object sender, RoutedEventArgs e)
    {
        Crop.Reset();
        StatusText.Text = "Crop reset – scan will capture full page";
    }
}
