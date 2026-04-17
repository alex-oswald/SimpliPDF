# SimpliPDF

A lightweight WinUI 3 desktop app for merging, reordering, and editing PDF pages.

## Features

- **Open & merge** multiple PDFs into one
- **Drag-and-drop** files from Windows Explorer
- **Reorder pages** by dragging thumbnails
- **Rotate** pages 90° clockwise
- **Delete** unwanted pages
- **Save Page** — save selected pages to a new PDF
- **Split All** — save every page as individual PDFs
- **Scan** — scan pages directly from a connected scanner
- **Print** the current document
- **Save Merged** — save all pages as a single PDF

## Screenshot

![SimpliPDF](https://github.com/user-attachments/assets/placeholder.png)

## Requirements

- Windows 10 (1809) or later
- .NET 10 SDK (for building from source)

## Installing from MSIX

The MSIX packages are signed with a self-signed development certificate.
Before installing, you need to trust the certificate once:

1. Download `SimpliPDF_Dev.cer` from this repo (or extract it from the MSIX)
2. Double-click it → **Install Certificate**
3. Select **Local Machine** → **Place all certificates in the following store** → **Trusted People**
4. Click Finish

Then double-click the `.msix` file to install.

## Building

```powershell
# Default build (ARM64 Debug)
.\build.ps1

# Release build for x64
.\build.ps1 -Architectures x64 -Configuration Release

# Build signed MSIX packages
.\build.ps1 -Architectures x64,arm64 -Configuration Release -Msix
```

Or open `SimpliPDF.slnx` in Visual Studio 2022+ and press F5.

## Tech Stack

| Component | Technology |
|-----------|-----------|
| UI Framework | WinUI 3 (Windows App SDK) |
| PDF Manipulation | [PDFsharp](https://github.com/empira/PDFsharp) (MIT) |
| Thumbnails | Windows.Data.Pdf |
| Scanning | WIA (Windows Image Acquisition) |
| Architecture | MVVM ([CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)) |
| Target | .NET 10, Windows 10+ |

## Project Structure

```
SimpliPDF.slnx                 Solution
SimpliPDF_Dev.pfx              Dev signing cert (no password)
SimpliPDF_Dev.cer              Public cert (for trusting)
build.ps1                      Build / publish script
SimpliPDF/
├── Models/PdfPageItem.cs       Page model
├── ViewModels/MainViewModel.cs MVVM view model
├── Services/
│   ├── PdfService.cs           PDFsharp merge / split / rotate
│   └── ScanService.cs          WIA scanner integration
├── Dialogs/
│   └── ScanDialog.xaml/.cs     Custom scan dialog
├── Helpers/
│   ├── ThumbnailHelper.cs      PDF page → BitmapImage
│   ├── PrintHelper.cs          WinUI 3 print support
│   └── Win32FileDialog.cs      Native file dialogs (COM)
├── MainWindow.xaml / .cs       App UI & code-behind
├── App.xaml / .cs              Entry point
└── Assets/                     Icons & logos
```

## License

MIT
