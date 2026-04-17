using Microsoft.UI.Xaml.Controls;
using SimplePDF.Services;

namespace SimplePDF.Dialogs;

public sealed partial class ScanDialog : ContentDialog
{
    public ScannerInfo? SelectedScanner { get; private set; }
    public int SelectedDpi { get; private set; } = 300;
    public ScanColorMode SelectedColorMode { get; private set; } = ScanColorMode.Color;

    /// <summary>Set to true if the dialog closed itself because no scanners were found.</summary>
    public bool NoScannersFound { get; private set; }

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
        ScannerCombo.SelectedIndex = 0; // Auto-select first scanner
    }

    private void OnScannerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedScanner = ScannerCombo.SelectedItem as ScannerInfo;
        IsPrimaryButtonEnabled = SelectedScanner != null;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (DpiCombo.SelectedItem is ComboBoxItem dpiItem && dpiItem.Tag is string dpiStr)
            SelectedDpi = int.Parse(dpiStr);

        if (ColorModeCombo.SelectedItem is ComboBoxItem colorItem && colorItem.Tag is string colorStr)
            SelectedColorMode = (ScanColorMode)int.Parse(colorStr);
    }
}
