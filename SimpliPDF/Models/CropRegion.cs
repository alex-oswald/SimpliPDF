namespace SimpliPDF.Models;

/// <summary>
/// A normalized crop rectangle (each value in the range 0..1) expressed in the page's
/// <em>native</em> render orientation — the orientation produced by <c>Windows.Data.Pdf</c>
/// (which honors the PDF's intrinsic <c>/Rotate</c>) but <em>before</em> any user rotation.
/// Origin is the top-left corner with Y pointing down.
/// </summary>
public record CropRegion(double Left, double Top, double Right, double Bottom)
{
    private const double Epsilon = 0.005;

    /// <summary>True when the region covers (essentially) the whole page.</summary>
    public bool IsFullPage =>
        Left <= Epsilon && Top <= Epsilon && Right >= 1 - Epsilon && Bottom >= 1 - Epsilon;

    /// <summary>Maps this native-space region into the orientation the user sees, given a
    /// clockwise user rotation in degrees (0/90/180/270).</summary>
    public CropRegion RotateForDisplay(int degrees) => Rotate(Normalize(degrees));

    /// <summary>Maps a displayed-space region back into native space (inverse of
    /// <see cref="RotateForDisplay"/>).</summary>
    public CropRegion RotateToNative(int degrees) => Rotate(Normalize(-degrees));

    private static int Normalize(int degrees)
    {
        int d = degrees % 360;
        if (d < 0) d += 360;
        return d;
    }

    private CropRegion Rotate(int degrees)
    {
        if (degrees == 0) return this;

        (double X, double Y)[] corners =
        [
            RotatePoint(Left, Top, degrees),
            RotatePoint(Right, Top, degrees),
            RotatePoint(Right, Bottom, degrees),
            RotatePoint(Left, Bottom, degrees),
        ];

        double minX = corners.Min(c => c.X);
        double minY = corners.Min(c => c.Y);
        double maxX = corners.Max(c => c.X);
        double maxY = corners.Max(c => c.Y);
        return new CropRegion(minX, minY, maxX, maxY);
    }

    // Clockwise rotation of a point within the unit square (origin top-left, Y down).
    private static (double X, double Y) RotatePoint(double x, double y, int degrees) => degrees switch
    {
        90 => (1 - y, x),
        180 => (1 - x, 1 - y),
        270 => (y, 1 - x),
        _ => (x, y),
    };
}
