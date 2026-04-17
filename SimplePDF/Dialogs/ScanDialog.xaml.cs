using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SimplePDF.Services;

namespace SimplePDF.Dialogs;

public sealed partial class ScanDialog : ContentDialog
{
    public ScannerInfo? SelectedScanner { get; private set; }
    public int SelectedDpi { get; private set; } = 300;
    public ScanColorMode SelectedColorMode { get; private set; } = ScanColorMode.Color;
    public bool NoScannersFound { get; private set; }

    /// <summary>Path to the scanned PDF, set after a successful scan.</summary>
    public string? ScannedPdfPath { get; private set; }

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

        SetBusy(true, "Previewing...");
        try
        {
            var imagePath = await ScanService.PreviewAsync(SelectedScanner.DeviceId);
            if (imagePath == null)
            {
                StatusText.Text = "Preview cancelled";
                return;
            }

            var bitmap = new BitmapImage();
            using var stream = File.OpenRead(imagePath);
            using var winrtStream = stream.AsRandomAccessStream();
            await bitmap.SetSourceAsync(winrtStream);

            PreviewImage.Source = bitmap;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            StatusText.Text = "Preview ready";

            try { File.Delete(imagePath); } catch { }
        }
        catch (Exception ex)
        {
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

            SetBusy(true, "Scanning...");

            var pdfPath = await ScanService.ScanAsync(
                SelectedScanner.DeviceId, SelectedDpi, SelectedColorMode);

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
        PreviewButton.IsEnabled = !busy && SelectedScanner != null;
        IsPrimaryButtonEnabled = !busy && SelectedScanner != null;
        CloseButtonText = busy ? "" : "Cancel";
        ScannerCombo.IsEnabled = !busy;
        DpiCombo.IsEnabled = !busy && DpiCombo.Items.Count > 0;
        ColorModeCombo.IsEnabled = !busy && ColorModeCombo.Items.Count > 0;

        if (status != null) StatusText.Text = status;
        else if (!busy) StatusText.Text = "";
    }
}
