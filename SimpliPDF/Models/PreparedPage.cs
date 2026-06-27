namespace SimpliPDF.Models;

/// <summary>
/// A page resolved for saving. Either imported directly from its source PDF
/// (vector/text preserved) or rasterized to an image-only page (used when a
/// destructive crop has been applied).
/// </summary>
public abstract record PreparedPage(int Rotation);

/// <summary>An uncropped page imported as-is from its source document.</summary>
public sealed record ImportedPage(string SourceFilePath, int OriginalPageIndex, int Rotation)
    : PreparedPage(Rotation);

/// <summary>
/// A cropped page rebuilt from a rasterized PNG of the cropped region. The
/// crop is baked into the pixels; <see cref="WidthPt"/>/<see cref="HeightPt"/>
/// are the cropped page size in points (displayed orientation).
/// </summary>
public sealed record RasterizedPage(byte[] Png, double WidthPt, double HeightPt, int Rotation)
    : PreparedPage(Rotation);
