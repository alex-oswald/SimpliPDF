using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpliPDF.Helpers;
using SimpliPDF.Models;
using SimpliPDF.Services;

namespace SimpliPDF.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly PdfService _pdfService = new();

    public ObservableCollection<PdfPageItem> Pages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPages))]
    public partial bool HasPages { get; set; }

    public bool HasNoPages => !HasPages;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Drop PDF files here or click Open";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public MainViewModel()
    {
        Pages.CollectionChanged += (_, _) =>
        {
            HasPages = Pages.Count > 0;
            UpdateStatus();
        };
    }

    public async Task LoadPdfAsync(string filePath)
    {
        int pageCount;
        try
        {
            pageCount = _pdfService.GetPageCount(filePath);
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading {Path.GetFileName(filePath)}: {ex.Message}";
            return;
        }

        var fileName = Path.GetFileName(filePath);

        for (int i = 0; i < pageCount; i++)
        {
            var item = new PdfPageItem
            {
                SourceFilePath = filePath,
                OriginalPageIndex = i,
                FileName = fileName
            };

            Pages.Add(item);

            try
            {
                item.Thumbnail = await ThumbnailHelper.RenderPageAsync(filePath, i);
            }
            catch
            {
                // Thumbnail generation failed — leave null
            }
        }
    }

    public async Task SaveToAsync(string outputPath)
    {
        if (Pages.Count == 0) return;
        try
        {
            await Task.Run(() => _pdfService.MergeAndSave(Pages.ToList(), outputPath));
            StatusText = $"Saved to {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error saving: {ex.Message}";
        }
    }

    public void DeletePages(IList<object> selectedItems)
    {
        var pages = selectedItems.OfType<PdfPageItem>().ToList();
        foreach (var p in pages)
            Pages.Remove(p);
    }

    public void RotatePages(IList<object> selectedItems)
    {
        foreach (var p in selectedItems.OfType<PdfPageItem>())
            p.Rotation = (p.Rotation + 90) % 360;
    }

    public async Task SplitToAsync(string outputFolder)
    {
        if (Pages.Count == 0) return;
        try
        {
            var count = Pages.Count;
            await Task.Run(() => _pdfService.Split(Pages.ToList(), outputFolder));
            StatusText = $"Split {count} pages to {Path.GetFileName(outputFolder)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error splitting: {ex.Message}";
        }
    }

    public async Task ExtractToAsync(IList<object> selectedItems, string outputPath)
    {
        var pages = selectedItems.OfType<PdfPageItem>().ToList();
        if (pages.Count == 0) return;
        try
        {
            await Task.Run(() => _pdfService.MergeAndSave(pages, outputPath));
            StatusText = $"Extracted {pages.Count} pages to {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error extracting: {ex.Message}";
        }
    }

    private void UpdateStatus()
    {
        StatusText = Pages.Count > 0
            ? $"{Pages.Count} page{(Pages.Count != 1 ? "s" : "")}"
            : "Drop PDF files here or click Open";
    }
}
