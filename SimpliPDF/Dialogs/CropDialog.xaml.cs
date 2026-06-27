using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpliPDF.Helpers;
using SimpliPDF.Models;

namespace SimpliPDF.Dialogs;

public sealed partial class CropDialog : ContentDialog
{
    private readonly PdfPageItem _page;

    /// <summary>The crop selected by the user in native page space, or <c>null</c> for no crop.
    /// Only valid after the dialog closes with <see cref="ContentDialogResult.Primary"/>.</summary>
    public CropRegion? Result { get; private set; }

    public CropDialog(PdfPageItem page)
    {
        _page = page;
        InitializeComponent();
        Crop.CropChanged += OnCropChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Render with the user's rotation baked in so the user crops what they actually see.
            BitmapImage bitmap = await ThumbnailHelper.RenderPageAsync(
                _page.SourceFilePath, _page.OriginalPageIndex, width: 1000u,
                crop: null, rotationDegrees: _page.Rotation);

            Crop.Source = bitmap;
            Crop.Visibility = Visibility.Visible;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;

            // Existing crop is stored in native space — show it in displayed space.
            CropRegion? initial = _page.Crop?.RotateForDisplay(_page.Rotation);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                Crop.SetCropRegion(initial);
                UpdateResetButton();
            });
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Failed to render page: {ex.Message}";
            IsPrimaryButtonEnabled = false;
        }
    }

    private void OnCropChanged(object? sender, EventArgs e) => UpdateResetButton();

    private void UpdateResetButton()
    {
        bool hasCrop = Crop.GetCropRegion() is not null;
        ResetCropButton.Visibility = hasCrop ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = hasCrop
            ? "This area is kept; everything outside it is removed when you save."
            : "Drag to select the area to keep.";
    }

    private void OnResetCropClick(object sender, RoutedEventArgs e) => Crop.Reset();

    private void OnApplyClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // The overlay reports the selection in displayed space — convert back to native space.
        CropRegion? region = Crop.GetCropRegion();
        Result = region?.RotateToNative(_page.Rotation);
    }
}
