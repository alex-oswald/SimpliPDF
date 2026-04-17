using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SimplePDF.Models;

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
    public partial BitmapImage? Thumbnail { get; set; }

    public string DisplayLabel => $"{FileName} – p.{OriginalPageIndex + 1}";

    public bool IsLandscape => Rotation % 180 != 0;
    public double CardWidth => IsLandscape ? 200 : 150;
    public double ImageHeight => IsLandscape ? 130 : 180;
}
