using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpliPDF.Dialogs;
using SimpliPDF.Helpers;
using SimpliPDF.Services;
using SimpliPDF.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SimpliPDF;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private const int MinWidthDip = 400;
    private const int MinHeightDip = 300;

    // Must be stored as a field to prevent GC collection of the delegate
    private readonly SUBCLASSPROC _subclassProc;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 750));

        // Install subclass to enforce minimum window size
        _subclassProc = SubclassProc;
        PInvoke.SetWindowSubclass(new HWND(Hwnd), _subclassProc, 0, 0);

        ViewModel.Pages.CollectionChanged += (_, _) => UpdateButtonStates();
        PagesGridView.SelectionChanged += (_, _) => UpdateButtonStates();
        UpdateButtonStates();

        _ = DiscoverScannersAsync();
    }

    private async Task DiscoverScannersAsync()
    {
        ViewModel.StatusText = "Discovering scanners...";
        try
        {
            List<ScannerInfo> scanners = await SimpliPDF.Services.ScanService.GetScannersAsync();
            ScanBtn.IsEnabled = true;
            ViewModel.StatusText = scanners.Count > 0
                ? $"Found {scanners.Count} scanner{(scanners.Count != 1 ? "s" : "")}"
                : "No scanners found";
        }
        catch
        {
            ScanBtn.IsEnabled = true;
            ViewModel.StatusText = "Scanner discovery failed";
        }
    }

    private LRESULT SubclassProc(HWND hWnd, uint uMsg, WPARAM wParam, LPARAM lParam, nuint uIdSubclass, nuint dwRefData)
    {
        const uint WM_GETMINMAXINFO = 0x0024;

        if (uMsg == WM_GETMINMAXINFO)
        {
            uint dpi = PInvoke.GetDpiForWindow(hWnd);
            double scale = dpi / 96.0;

            MINMAXINFO minMax = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
            minMax.ptMinTrackSize.X = (int)(MinWidthDip * scale);
            minMax.ptMinTrackSize.Y = (int)(MinHeightDip * scale);
            System.Runtime.InteropServices.Marshal.StructureToPtr(minMax, lParam, false);
        }

        return PInvoke.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void UpdateButtonStates()
    {
        int pageCount = ViewModel.Pages.Count;
        int selectedCount = PagesGridView.SelectedItems.Count;
        bool hasPages = pageCount > 0;
        bool hasSelection = selectedCount > 0;

        // Empty state
        EmptyState.Visibility = hasPages ? Visibility.Collapsed : Visibility.Visible;

        // Buttons that need pages
        SaveBtn.IsEnabled = hasPages;
        PrintBtn.IsEnabled = hasPages;

        // Buttons that need selection
        DeleteBtn.IsEnabled = hasSelection;
        RotateBtn.IsEnabled = hasSelection;
        ExtractBtn.IsEnabled = hasSelection;
        DeselectBtn.IsEnabled = hasSelection;

        // Crop works on exactly one page at a time
        CropBtn.IsEnabled = selectedCount == 1;

        // Split needs 2+ pages
        SplitBtn.IsEnabled = pageCount >= 2;
    }

    // --- File operations using Win32 dialogs ---

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string[]? files = Win32FileDialog.OpenPdfFiles(Hwnd);
            if (files == null || files.Length == 0) return;

            ViewModel.IsLoading = true;
            try
            {
                foreach (string path in files)
                    await ViewModel.LoadPdfAsync(path);
            }
            finally
            {
                ViewModel.IsLoading = false;
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Open error: {ex.Message}";
        }
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ScanDialog dialog = new SimpliPDF.Dialogs.ScanDialog { XamlRoot = Content.XamlRoot };
            ContentDialogResult result = await dialog.ShowAsync();

            if (dialog.NoScannersFound)
            {
                ContentDialog errorDialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "No scanners found",
                    Content = "Connect a scanner to your computer and try again.",
                    CloseButtonText = "OK",
                };
                await errorDialog.ShowAsync();
                return;
            }

            if (result != ContentDialogResult.Primary || dialog.ScannedPdfPath == null)
                return;

            ViewModel.IsLoading = true;
            try
            {
                await ViewModel.LoadPdfAsync(dialog.ScannedPdfPath);
                ViewModel.StatusText = "Scanned page added";
            }
            finally
            {
                ViewModel.IsLoading = false;
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Scan error: {ex.Message}";
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Pages.Count == 0) return;
        try
        {
            string? path = Win32FileDialog.SavePdfFile(Hwnd);
            if (path == null) return;
            await ViewModel.SaveToAsync(path);
        }
        catch (Exception ex) { ViewModel.StatusText = $"Save error: {ex.Message}"; }
    }

    private async void OnPrintClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Pages.Count == 0) return;
        try
        {
            PrintHelper helper = new PrintHelper(Hwnd);
            await helper.PrintAsync(ViewModel.Pages.ToList());
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Print error: {ex.Message}";
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (PagesGridView.SelectedItems.Count == 0) return;
        ViewModel.DeletePages(PagesGridView.SelectedItems);
    }

    private void OnRotateClick(object sender, RoutedEventArgs e)
    {
        if (PagesGridView.SelectedItems.Count == 0) return;
        ViewModel.RotatePages(PagesGridView.SelectedItems);
    }

    private async void OnCropClick(object sender, RoutedEventArgs e)
    {
        if (PagesGridView.SelectedItems.Count != 1) return;
        if (PagesGridView.SelectedItems[0] is not Models.PdfPageItem page) return;

        try
        {
            CropDialog dialog = new SimpliPDF.Dialogs.CropDialog(page) { XamlRoot = Content.XamlRoot };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            await ViewModel.ApplyCropAsync(page, dialog.Result);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Crop error: {ex.Message}";
        }
    }

    private void OnDeselectAllClick(object sender, RoutedEventArgs e)
    {
        PagesGridView.SelectedItems.Clear();
    }

    private async void OnSplitClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Pages.Count == 0) return;
        try
        {
            string? folder = Win32FileDialog.PickFolder(Hwnd);
            if (folder == null) return;
            await ViewModel.SplitToAsync(folder);
        }
        catch (Exception ex) { ViewModel.StatusText = $"Split error: {ex.Message}"; }
    }

    private async void OnExtractClick(object sender, RoutedEventArgs e)
    {
        if (PagesGridView.SelectedItems.Count == 0)
        {
            ViewModel.StatusText = "Select pages to extract first";
            return;
        }
        try
        {
            string? path = Win32FileDialog.SavePdfFile(Hwnd, "Extracted.pdf");
            if (path == null) return;
            await ViewModel.ExtractToAsync(PagesGridView.SelectedItems, path);
        }
        catch (Exception ex) { ViewModel.StatusText = $"Extract error: {ex.Message}"; }
    }

    // --- Explorer drag-and-drop ---

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add PDF pages";
            e.DragUIOverride.IsCaptionVisible = true;
            e.Handled = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;

        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            ViewModel.IsLoading = true;
            try
            {
                foreach (IStorageItem? item in items)
                {
                    if (item is StorageFile file &&
                        file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        await ViewModel.LoadPdfAsync(file.Path);
                    }
                }
            }
            finally
            {
                ViewModel.IsLoading = false;
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Drop error: {ex.Message}";
        }
    }
}
