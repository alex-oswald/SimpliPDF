# SimplePDF

A lightweight WinUI 3 desktop app for merging, reordering, and editing PDF pages.

## Features

- **Open & merge** multiple PDFs into one
- **Drag-and-drop** files from Windows Explorer
- **Reorder pages** by dragging thumbnails
- **Rotate** pages 90° clockwise
- **Delete** unwanted pages
- **Extract** selected pages to a new PDF
- **Split** every page into individual PDFs
- **Print** the current document
- **Save** the merged result as a new PDF

## Screenshot

![SimplePDF](https://github.com/user-attachments/assets/placeholder.png)

## Requirements

- Windows 10 (1809) or later
- .NET 10 SDK (for building from source)

## Building

```powershell
# Default build (ARM64 Debug)
.\build.ps1

# Release build for x64
.\build.ps1 -Architectures x64 -Configuration Release

# Multi-arch publish
.\build.ps1 -Architectures x64,arm64 -Configuration Release -Publish
```

Or open `SimplePDF.slnx` in Visual Studio 2022+ and press F5.

## Tech Stack

| Component | Technology |
|-----------|-----------|
| UI Framework | WinUI 3 (Windows App SDK) |
| PDF Manipulation | [PDFsharp](https://github.com/empira/PDFsharp) (MIT) |
| Thumbnails | Windows.Data.Pdf |
| Architecture | MVVM ([CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)) |
| Target | .NET 10, Windows 10+ |

## Project Structure

```
SimplePDF.slnx                    Solution
build.ps1                      Build / publish script
SimplePDF/
├── Models/PdfPageItem.cs       Page model
├── ViewModels/MainViewModel.cs MVVM view model
├── Services/PdfService.cs      PDFsharp merge / split / rotate
├── Helpers/
│   ├── ThumbnailHelper.cs      PDF page → BitmapImage
│   ├── PrintHelper.cs          WinUI 3 print support
│   └── Win32FileDialog.cs      Native file dialogs
├── MainWindow.xaml / .cs       App UI & code-behind
├── App.xaml / .cs              Entry point
└── Assets/                     Icons & logos
```

## License

MIT
