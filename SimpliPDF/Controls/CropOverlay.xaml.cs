using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpliPDF.Models;
using Windows.Foundation;

namespace SimpliPDF.Controls;

/// <summary>
/// A reusable image preview with an interactive crop rectangle (drag to create, drag edges to
/// resize). Reports the selection as a normalized <see cref="CropRegion"/> relative to the
/// rendered image bounds. The control is orientation-agnostic — callers that bake a rotation into
/// the <see cref="Source"/> bitmap are responsible for translating the region afterward.
/// </summary>
public sealed partial class CropOverlay : UserControl
{
    private Point _dragStart;
    private CropRegion? _cropRegion;

    [Flags]
    private enum DragEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }
    private enum DragMode { None, Resize, Create }

    private DragMode _dragMode;
    private DragEdge _dragEdges;
    private const double HandleMargin = 10;
    private const double MinCropSize = 20;
    private const double FullThreshold = 0.02;

    /// <summary>Raised whenever the user changes the crop selection (create, resize, or reset).</summary>
    public event EventHandler? CropChanged;

    public CropOverlay()
    {
        InitializeComponent();
        CropCanvas.SizeChanged += OnCanvasSizeChanged;
    }

    /// <summary>
    /// Re-anchors the crop rectangle to the stored normalized region whenever the canvas is
    /// resized. The rectangle is positioned in absolute canvas pixels, so without this it would
    /// drift off the image when the layout changes (e.g. the scan dialog's progress row appearing,
    /// or the window being resized).
    /// </summary>
    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewImage.Source is null || CropRect.Visibility == Visibility.Collapsed)
            return;

        if (_cropRegion is null)
            InitializeToFull();
        else
            SetCropRegion(_cropRegion);
    }

    /// <summary>The image to display and crop.</summary>
    public ImageSource? Source
    {
        get => PreviewImage.Source;
        set => PreviewImage.Source = value;
    }

    /// <summary>Sets the crop rectangle to cover the whole image (i.e. no crop).</summary>
    public void InitializeToFull()
    {
        Rect bounds = GetImageBounds();
        Canvas.SetLeft(CropRect, bounds.X);
        Canvas.SetTop(CropRect, bounds.Y);
        CropRect.Width = bounds.Width;
        CropRect.Height = bounds.Height;
        CropRect.Visibility = Visibility.Visible;

        _cropRegion = null;
        UpdateCropVisuals();
    }

    /// <summary>Positions the crop rectangle to match an existing normalized region.</summary>
    public void SetCropRegion(CropRegion? region)
    {
        if (region is null || region.IsFullPage)
        {
            InitializeToFull();
            return;
        }

        Rect bounds = GetImageBounds();
        Canvas.SetLeft(CropRect, bounds.X + region.Left * bounds.Width);
        Canvas.SetTop(CropRect, bounds.Y + region.Top * bounds.Height);
        CropRect.Width = (region.Right - region.Left) * bounds.Width;
        CropRect.Height = (region.Bottom - region.Top) * bounds.Height;
        CropRect.Visibility = Visibility.Visible;

        _cropRegion = region;
        UpdateCropVisuals();
    }

    /// <summary>The current selection, or <c>null</c> when the whole image is selected.</summary>
    public CropRegion? GetCropRegion() => _cropRegion;

    /// <summary>Resets the selection to the full image and raises <see cref="CropChanged"/>.</summary>
    public void Reset()
    {
        InitializeToFull();
        CropChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Crop interaction ──────────────────────────────────────────

    private DragEdge HitTestEdges(Point pos)
    {
        if (CropRect.Visibility == Visibility.Collapsed) return DragEdge.None;

        double cx = Canvas.GetLeft(CropRect);
        double cy = Canvas.GetTop(CropRect);
        double cr = cx + CropRect.Width;
        double cb = cy + CropRect.Height;

        // Must be within extended rect area
        if (pos.X < cx - HandleMargin || pos.X > cr + HandleMargin ||
            pos.Y < cy - HandleMargin || pos.Y > cb + HandleMargin)
            return DragEdge.None;

        DragEdge edges = DragEdge.None;
        if (Math.Abs(pos.X - cx) <= HandleMargin) edges |= DragEdge.Left;
        else if (Math.Abs(pos.X - cr) <= HandleMargin) edges |= DragEdge.Right;

        if (Math.Abs(pos.Y - cy) <= HandleMargin) edges |= DragEdge.Top;
        else if (Math.Abs(pos.Y - cb) <= HandleMargin) edges |= DragEdge.Bottom;

        return edges;
    }

    /// <summary>Maps the hovered/dragged edge combination to the matching resize cursor.</summary>
    private static InputSystemCursorShape CursorForEdges(DragEdge edges) => edges switch
    {
        DragEdge.Left | DragEdge.Top => InputSystemCursorShape.SizeNorthwestSoutheast,
        DragEdge.Right | DragEdge.Bottom => InputSystemCursorShape.SizeNorthwestSoutheast,
        DragEdge.Right | DragEdge.Top => InputSystemCursorShape.SizeNortheastSouthwest,
        DragEdge.Left | DragEdge.Bottom => InputSystemCursorShape.SizeNortheastSouthwest,
        DragEdge.Left or DragEdge.Right => InputSystemCursorShape.SizeWestEast,
        DragEdge.Top or DragEdge.Bottom => InputSystemCursorShape.SizeNorthSouth,
        _ => InputSystemCursorShape.Cross,
    };

    private void OnCropPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None)
            CropCanvas.ResetCursor();
    }

    private void OnCropPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (PreviewImage.Source is null) return;

        Point pos = e.GetCurrentPoint(CropCanvas).Position;
        CropCanvas.CapturePointer(e.Pointer);

        _dragEdges = HitTestEdges(pos);
        if (_dragEdges != DragEdge.None)
        {
            _dragMode = DragMode.Resize;
            CropCanvas.SetCursorShape(CursorForEdges(_dragEdges));
        }
        else
        {
            _dragMode = DragMode.Create;
            CropCanvas.SetCursorShape(InputSystemCursorShape.Cross);
            _dragStart = pos;
            Canvas.SetLeft(CropRect, pos.X);
            Canvas.SetTop(CropRect, pos.Y);
            CropRect.Width = 0;
            CropRect.Height = 0;
            CropRect.Visibility = Visibility.Visible;
        }
    }

    private void OnCropPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Point pos = e.GetCurrentPoint(CropCanvas).Position;

        if (_dragMode == DragMode.None)
        {
            if (PreviewImage.Source is not null)
                CropCanvas.SetCursorShape(CursorForEdges(HitTestEdges(pos)));
            return;
        }

        double canvasW = CropCanvas.ActualWidth;
        double canvasH = CropCanvas.ActualHeight;
        pos = new Point(Math.Clamp(pos.X, 0, canvasW), Math.Clamp(pos.Y, 0, canvasH));

        if (_dragMode == DragMode.Create)
        {
            double x = Math.Min(_dragStart.X, pos.X);
            double y = Math.Min(_dragStart.Y, pos.Y);
            double w = Math.Abs(pos.X - _dragStart.X);
            double h = Math.Abs(pos.Y - _dragStart.Y);

            Canvas.SetLeft(CropRect, x);
            Canvas.SetTop(CropRect, y);
            CropRect.Width = w;
            CropRect.Height = h;
        }
        else
        {
            double left = Canvas.GetLeft(CropRect);
            double top = Canvas.GetTop(CropRect);
            double right = left + CropRect.Width;
            double bottom = top + CropRect.Height;

            if (_dragEdges.HasFlag(DragEdge.Left)) left = Math.Min(pos.X, right - MinCropSize);
            if (_dragEdges.HasFlag(DragEdge.Right)) right = Math.Max(pos.X, left + MinCropSize);
            if (_dragEdges.HasFlag(DragEdge.Top)) top = Math.Min(pos.Y, bottom - MinCropSize);
            if (_dragEdges.HasFlag(DragEdge.Bottom)) bottom = Math.Max(pos.Y, top + MinCropSize);

            Canvas.SetLeft(CropRect, Math.Max(0, left));
            Canvas.SetTop(CropRect, Math.Max(0, top));
            CropRect.Width = Math.Min(right - Math.Max(0, left), canvasW - Math.Max(0, left));
            CropRect.Height = Math.Min(bottom - Math.Max(0, top), canvasH - Math.Max(0, top));
        }

        UpdateCropVisuals();
    }

    private void OnCropPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CropCanvas.ReleasePointerCapture(e.Pointer);
        if (_dragMode == DragMode.None) return;
        _dragMode = DragMode.None;

        if (CropRect.Width < MinCropSize || CropRect.Height < MinCropSize)
        {
            InitializeToFull();
            CropChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        ComputeCropRegion();
    }

    private void ComputeCropRegion()
    {
        Rect imageBounds = GetImageBounds();
        if (imageBounds.Width <= 0 || imageBounds.Height <= 0) return;

        double left = Math.Clamp((Canvas.GetLeft(CropRect) - imageBounds.X) / imageBounds.Width, 0, 1);
        double top = Math.Clamp((Canvas.GetTop(CropRect) - imageBounds.Y) / imageBounds.Height, 0, 1);
        double right = Math.Clamp(left + CropRect.Width / imageBounds.Width, 0, 1);
        double bottom = Math.Clamp(top + CropRect.Height / imageBounds.Height, 0, 1);

        // If essentially the full image, treat as no crop.
        if (left < FullThreshold && top < FullThreshold &&
            right > 1 - FullThreshold && bottom > 1 - FullThreshold)
            _cropRegion = null;
        else
            _cropRegion = new CropRegion(left, top, right, bottom);

        CropChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCropVisuals()
    {
        double x = Canvas.GetLeft(CropRect);
        double y = Canvas.GetTop(CropRect);
        double w = CropRect.Width;
        double h = CropRect.Height;
        double canvasW = CropCanvas.ActualWidth;
        double canvasH = CropCanvas.ActualHeight;

        UpdateDimOverlays(x, y, w, h, canvasW, canvasH);
        UpdateHandlePositions(x, y, w, h);
        SetHandlesVisibility(Visibility.Visible);
    }

    private void UpdateDimOverlays(double x, double y, double w, double h, double canvasW, double canvasH)
    {
        Canvas.SetLeft(DimTop, 0);
        Canvas.SetTop(DimTop, 0);
        DimTop.Width = canvasW;
        DimTop.Height = y;

        Canvas.SetLeft(DimBottom, 0);
        Canvas.SetTop(DimBottom, y + h);
        DimBottom.Width = canvasW;
        DimBottom.Height = Math.Max(0, canvasH - y - h);

        Canvas.SetLeft(DimLeft, 0);
        Canvas.SetTop(DimLeft, y);
        DimLeft.Width = x;
        DimLeft.Height = h;

        Canvas.SetLeft(DimRight, x + w);
        Canvas.SetTop(DimRight, y);
        DimRight.Width = Math.Max(0, canvasW - x - w);
        DimRight.Height = h;
    }

    private void UpdateHandlePositions(double x, double y, double w, double h)
    {
        const double hs = 10;
        const double hh = hs / 2;

        PositionHandle(HandleTL, x - hh, y - hh);
        PositionHandle(HandleT, x + w / 2 - hh, y - hh);
        PositionHandle(HandleTR, x + w - hh, y - hh);
        PositionHandle(HandleL, x - hh, y + h / 2 - hh);
        PositionHandle(HandleR, x + w - hh, y + h / 2 - hh);
        PositionHandle(HandleBL, x - hh, y + h - hh);
        PositionHandle(HandleB, x + w / 2 - hh, y + h - hh);
        PositionHandle(HandleBR, x + w - hh, y + h - hh);
    }

    private static void PositionHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
    }

    private void SetHandlesVisibility(Visibility v)
    {
        HandleTL.Visibility = v;
        HandleT.Visibility = v;
        HandleTR.Visibility = v;
        HandleL.Visibility = v;
        HandleR.Visibility = v;
        HandleBL.Visibility = v;
        HandleB.Visibility = v;
        HandleBR.Visibility = v;
    }

    /// <summary>
    /// Computes the rendered bounds of the preview image within the canvas. The image uses
    /// <c>Stretch="Uniform"</c>, so it is letterboxed inside its container.
    /// </summary>
    private Rect GetImageBounds()
    {
        if (PreviewImage.Source is not BitmapImage bmp || bmp.PixelWidth == 0 || bmp.PixelHeight == 0)
            return new Rect(0, 0, CropCanvas.ActualWidth, CropCanvas.ActualHeight);

        double canvasW = CropCanvas.ActualWidth;
        double canvasH = CropCanvas.ActualHeight;
        double imageAspect = (double)bmp.PixelWidth / bmp.PixelHeight;
        double canvasAspect = canvasW / canvasH;

        double renderW;
        double renderH;
        if (imageAspect > canvasAspect)
        {
            renderW = canvasW;
            renderH = canvasW / imageAspect;
        }
        else
        {
            renderH = canvasH;
            renderW = canvasH * imageAspect;
        }

        double offsetX = (canvasW - renderW) / 2;
        double offsetY = (canvasH - renderH) / 2;

        return new Rect(offsetX, offsetY, renderW, renderH);
    }
}
