using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SimplePDF.Models;

public partial class PdfPageItem : ObservableObject
{
    public required string SourceFilePath { get; init; }
    public required int OriginalPageIndex { get; init; }
    public required string FileName { get; init; }

    [ObservableProperty]
    public partial int Rotation { get; set; }

    [ObservableProperty]
    public partial BitmapImage? Thumbnail { get; set; }

    public string DisplayLabel => $"{FileName} – p.{OriginalPageIndex + 1}";
}
