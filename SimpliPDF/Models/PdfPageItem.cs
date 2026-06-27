using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SimpliPDF.Models;

public partial class PdfPageItem : ObservableObject
{
    public required string SourceFilePath { get; init; }
    public required int OriginalPageIndex { get; init; }
    public required string FileName { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLandscape))]
    [NotifyPropertyChangedFor(nameof(CardWidth))]
    [NotifyPropertyChangedFor(nameof(ImageHeight))]
    public partial int Rotation { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayThumbnail))]
    public partial BitmapImage? Thumbnail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayThumbnail))]
    public partial BitmapImage? CroppedThumbnail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrop))]
    public partial CropRegion? Crop { get; set; }

    /// <summary>The image shown on the page card — the cropped render when present.</summary>
    public BitmapImage? DisplayThumbnail => CroppedThumbnail ?? Thumbnail;

    public bool HasCrop => Crop is not null;

    public string DisplayLabel => $"{FileName} – p.{OriginalPageIndex + 1}";

    public bool IsLandscape => Rotation % 180 != 0;
    public double CardWidth => IsLandscape ? 200 : 150;
    public double ImageHeight => IsLandscape ? 130 : 180;
}
