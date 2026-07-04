using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SimpliPDF.Models;

public partial class PdfPageItem : ObservableObject
{
    public required string SourceFilePath { get; init; }
    public required int OriginalPageIndex { get; init; }
    public required string FileName { get; init; }

    [ObservableProperty]
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

    /// <summary>Creates an independent copy that renders the same source page, preserving the
    /// user's rotation and crop. Thumbnails are shared so the duplicate appears immediately.</summary>
    public PdfPageItem Clone() => new()
    {
        SourceFilePath = SourceFilePath,
        OriginalPageIndex = OriginalPageIndex,
        FileName = FileName,
        Rotation = Rotation,
        Thumbnail = Thumbnail,
        CroppedThumbnail = CroppedThumbnail,
        Crop = Crop,
    };
}
