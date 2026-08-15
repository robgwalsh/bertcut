using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BertCut.Core.Input;
using BertCut.Core.Model;

namespace BertCut.App;

/// <summary>
/// The draggable rectangle shown over the preview while a crop or overlay is placed.
/// </summary>
/// <remarks>
/// <para>
/// This is the case that ruled out hosting the preview in a child window: crop handles and
/// picture-in-picture boxes are WPF content that has to sit <em>on top of</em> the video,
/// which an airspace-bound child HWND forbids outright.
/// </para>
/// <para>
/// The control works in output-space pixels and converts to screen coordinates only when
/// drawing and hit-testing, so what the user positions is literally the rectangle the
/// ffmpeg filter graph receives — no rounding creeps in between the two.
/// </para>
/// </remarks>
public sealed class RectEditor : FrameworkElement
{
    private static readonly Pen EdgePen = FrozenPen(Color.FromRgb(0x4F, 0x9C, 0xF5), 2);
    private static readonly Pen GuidePen = FrozenPen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0x4F, 0x9C, 0xF5));
    private static readonly Brush ShadeBrush = Frozen(Color.FromArgb(0x88, 0x00, 0x00, 0x00));

    /// <summary>Corner handle size, in screen pixels.</summary>
    private const double HandleSize = 10;

    /// <summary>
    /// How far the pointer must travel before a press outside the box starts drawing a new
    /// one, in screen pixels.
    /// </summary>
    /// <remarks>
    /// Without it, the press alone collapses the rectangle to the minimum size at the
    /// cursor — so a stray click, or a click meant to give the preview focus, destroys the
    /// placement the user had already made.
    /// </remarks>
    private const double DragThreshold = 4;

    /// <summary>What the pointer is doing between press and release.</summary>
    private enum DragKind
    {
        None,

        /// <summary>Moving the whole rectangle.</summary>
        Move,

        /// <summary>Resizing from a corner handle, with the opposite corner pinned.</summary>
        Resize,

        /// <summary>Drawing a replacement rectangle, once the pointer has actually moved.</summary>
        DrawNew,
    }

    private EditorViewModel? _model;
    private DragKind _drag;
    private Point _dragOrigin;
    private RectI _dragStartRect;
    private (int X, int Y) _resizeAnchor;

    public RectEditor()
    {
        // Only interactive while placing, so it never steals clicks from the timeline.
        Focusable = false;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
    }

    public void Bind(EditorViewModel model)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;

        _model = model;
        _model.PropertyChanged += OnModelChanged;
        Sync();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.Mode)
            or nameof(EditorViewModel.IsPlacing)
            or nameof(EditorViewModel.PendingRect)
            or nameof(EditorViewModel.Project))
        {
            Sync();
        }
    }

    private void Sync()
    {
        var placing = _model?.IsPlacing == true;

        // Esc can end a placement mid-drag, which would otherwise leave the capture and
        // the drag state behind for the next one to inherit.
        if (!placing && _drag != DragKind.None)
        {
            _drag = DragKind.None;
            ReleaseMouseCapture();
        }

        Visibility = placing ? Visibility.Visible : Visibility.Collapsed;
        IsHitTestVisible = placing;
        InvalidateVisual();
    }

    /// <summary>
    /// Where the video actually sits inside this element.
    /// </summary>
    /// <remarks>
    /// The preview is letterboxed by <c>Stretch="Uniform"</c>, so the video rectangle is
    /// not the element rectangle. Everything below maps through this; getting it wrong
    /// would put the crop box somewhere other than where it crops.
    /// </remarks>
    private Rect VideoBounds()
    {
        if (_model is null) return new Rect(0, 0, ActualWidth, ActualHeight);

        var output = _model.Project.Output;
        if (output.Width <= 0 || output.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return new Rect(0, 0, ActualWidth, ActualHeight);

        var scale = Math.Min(ActualWidth / output.Width, ActualHeight / output.Height);
        var w = output.Width * scale;
        var h = output.Height * scale;

        return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
    }

    private Rect ToScreen(RectI rect)
    {
        var bounds = VideoBounds();
        var output = _model!.Project.Output;
        var scaleX = bounds.Width / output.Width;
        var scaleY = bounds.Height / output.Height;

        return new Rect(
            bounds.X + (rect.X * scaleX),
            bounds.Y + (rect.Y * scaleY),
            rect.W * scaleX,
            rect.H * scaleY);
    }

    private (int X, int Y) ToOutput(Point point)
    {
        var bounds = VideoBounds();
        var output = _model!.Project.Output;

        var x = (point.X - bounds.X) / Math.Max(1, bounds.Width) * output.Width;
        var y = (point.Y - bounds.Y) / Math.Max(1, bounds.Height) * output.Height;

        return ((int)Math.Round(x), (int)Math.Round(y));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_model is null || !_model.IsPlacing) return;

        var bounds = VideoBounds();
        var rect = ToScreen(_model.PendingRect);

        // WPF hit-tests painted geometry, not element bounds, so this transparent fill is
        // what makes the surface clickable at all. Without it the inside of the box — the
        // part you grab to move it — is a hole the press falls straight through to the
        // video underneath, and dragging the box does nothing whatsoever.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        // Shade everything outside the rectangle so the selection reads at a glance.
        var outside = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(bounds),
            new RectangleGeometry(rect));

        dc.DrawGeometry(ShadeBrush, null, outside);
        dc.DrawRectangle(null, EdgePen, rect);

        // Thirds, which is how people actually judge framing.
        for (var i = 1; i < 3; i++)
        {
            var x = rect.X + (rect.Width * i / 3);
            var y = rect.Y + (rect.Height * i / 3);
            dc.DrawLine(GuidePen, new Point(x, rect.Y), new Point(x, rect.Bottom));
            dc.DrawLine(GuidePen, new Point(rect.X, y), new Point(rect.Right, y));
        }

        foreach (var corner in Corners(rect))
            dc.DrawRectangle(HandleBrush, null,
                new Rect(corner.X - (HandleSize / 2), corner.Y - (HandleSize / 2), HandleSize, HandleSize));
    }

    private static Point[] Corners(Rect r) =>
    [
        new(r.X, r.Y), new(r.Right, r.Y), new(r.X, r.Bottom), new(r.Right, r.Bottom),
    ];

    /// <summary>Which corner handle the pointer is over, or null.</summary>
    private static int? CornerAt(Rect rect, Point point)
    {
        var corners = Corners(rect);

        for (var i = 0; i < corners.Length; i++)
            if (Math.Abs(point.X - corners[i].X) <= HandleSize
                && Math.Abs(point.Y - corners[i].Y) <= HandleSize)
                return i;

        return null;
    }

    /// <summary>The corner that stays put while <paramref name="corner"/> is dragged.</summary>
    private static (int X, int Y) OppositeCorner(RectI rect, int corner) => corner switch
    {
        0 => (rect.Right, rect.Bottom),
        1 => (rect.X, rect.Bottom),
        2 => (rect.Right, rect.Y),
        _ => (rect.X, rect.Y),
    };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_model is null || !_model.IsPlacing) return;

        // Double-click commits, so a mouse-led placement never has to reach for Enter.
        if (e.ClickCount == 2)
        {
            _model.Dispatch(EditorIntent.Commit);
            e.Handled = true;
            return;
        }

        var point = e.GetPosition(this);
        var rect = ToScreen(_model.PendingRect);

        CaptureMouse();
        _dragOrigin = point;
        _dragStartRect = _model.PendingRect;

        // A corner handle resizes; inside the box moves it; anywhere else draws a new one,
        // so a user who wants a completely different region does not have to drag the old
        // one there. Drawing waits for the pointer to move — see DragThreshold.
        if (CornerAt(rect, point) is { } corner)
        {
            _drag = DragKind.Resize;
            _resizeAnchor = OppositeCorner(_dragStartRect, corner);
        }
        else
        {
            _drag = rect.Contains(point) ? DragKind.Move : DragKind.DrawNew;
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_model is null || !_model.IsPlacing) return;

        var point = e.GetPosition(this);

        if (_drag == DragKind.None)
        {
            Cursor = CursorFor(point);
            return;
        }

        switch (_drag)
        {
            case DragKind.Resize:
                var (x, y) = ToOutput(point);
                _model.ResizePendingRect(_resizeAnchor.X, _resizeAnchor.Y, x, y);
                break;

            case DragKind.Move:
                var bounds = VideoBounds();
                var output = _model.Project.Output;

                var dx = (int)Math.Round((point.X - _dragOrigin.X) / Math.Max(1, bounds.Width) * output.Width);
                var dy = (int)Math.Round((point.Y - _dragOrigin.Y) / Math.Max(1, bounds.Height) * output.Height);

                _model.SetPendingRect(_dragStartRect with { X = _dragStartRect.X + dx, Y = _dragStartRect.Y + dy });
                break;

            case DragKind.DrawNew:
                if (Math.Abs(point.X - _dragOrigin.X) < DragThreshold
                    && Math.Abs(point.Y - _dragOrigin.Y) < DragThreshold)
                    return;

                var (x0, y0) = ToOutput(_dragOrigin);
                var (x1, y1) = ToOutput(point);
                _model.DragPendingRect(x0, y0, x1, y1);
                break;
        }

        e.Handled = true;
    }

    /// <summary>The shape of the gesture the pointer is currently offering.</summary>
    private Cursor CursorFor(Point point)
    {
        var rect = ToScreen(_model!.PendingRect);

        return CornerAt(rect, point) switch
        {
            0 or 3 => Cursors.SizeNWSE,
            1 or 2 => Cursors.SizeNESW,
            _ => rect.Contains(point) ? Cursors.SizeAll : Cursors.Cross,
        };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == DragKind.None) return;

        _drag = DragKind.None;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>Ends a drag that lost the mouse, e.g. to a window that stole capture.</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _drag = DragKind.None;
        base.OnLostMouseCapture(e);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
