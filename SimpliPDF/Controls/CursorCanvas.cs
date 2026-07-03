using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace SimpliPDF.Controls;

/// <summary>
/// A <see cref="Canvas"/> that exposes its protected cursor so a host can show resize cursors
/// while hovering crop handles. <see cref="UIElement.ProtectedCursor"/> is only settable from a
/// derived type, hence this thin subclass. Created cursors are cached and reused.
/// </summary>
public sealed class CursorCanvas : Canvas
{
    private readonly Dictionary<InputSystemCursorShape, InputCursor> _cache = [];
    private InputSystemCursorShape? _current;

    /// <summary>Shows the system cursor for <paramref name="shape"/> over this canvas.</summary>
    public void SetCursorShape(InputSystemCursorShape shape)
    {
        if (_current == shape) return;
        _current = shape;

        if (!_cache.TryGetValue(shape, out InputCursor? cursor))
        {
            cursor = InputSystemCursor.Create(shape);
            _cache[shape] = cursor;
        }
        ProtectedCursor = cursor;
    }

    /// <summary>Restores the default cursor.</summary>
    public void ResetCursor()
    {
        _current = null;
        ProtectedCursor = null;
    }
}
