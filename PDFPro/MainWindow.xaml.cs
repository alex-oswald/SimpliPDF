using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFPro.Helpers;
using PDFPro.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PDFPro;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 750));

        ViewModel.Pages.CollectionChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var hasPages = ViewModel.Pages.Count > 0;
        EmptyState.Visibility = hasPages ? Visibility.Collapsed : Visibility.Visible;
        PagesGridView.Visibility = hasPages ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- File operations using Win32 dialogs ---

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = Win32FileDialog.OpenPdfFiles(Hwnd);
            if (files == null || files.Length == 0) return;

            ViewModel.IsLoading = true;
            try
            {
                foreach (var path in files)
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

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Pages.Count == 0) return;
        try
        {
            var path = Win32FileDialog.SavePdfFile(Hwnd);
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
            var helper = new PrintHelper(Hwnd);
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

    private async void OnSplitClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Pages.Count == 0) return;
        try
        {
            var folder = Win32FileDialog.PickFolder(Hwnd);
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
            var path = Win32FileDialog.SavePdfFile(Hwnd, "Extracted.pdf");
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
            var items = await e.DataView.GetStorageItemsAsync();
            ViewModel.IsLoading = true;
            try
            {
                foreach (var item in items)
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
