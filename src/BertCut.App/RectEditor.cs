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

    private EditorViewModel? _model;
    private bool _dragging;
    private bool _drawingNew;
    private Point _dragOrigin;
    private RectI _dragStartRect;

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
        _dragging = true;
        _dragOrigin = point;
        _dragStartRect = _model.PendingRect;

        // Inside the box moves it; anywhere else starts drawing a new one, so a user who
        // wants a completely different region does not have to drag the old one there.
        _drawingNew = !rect.Contains(point);

        if (_drawingNew)
        {
            var (x, y) = ToOutput(point);
            _model.DragPendingRect(x, y, x, y);
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging || _model is null) return;

        var point = e.GetPosition(this);

        if (_drawingNew)
        {
            var (x0, y0) = ToOutput(_dragOrigin);
            var (x1, y1) = ToOutput(point);
            _model.DragPendingRect(x0, y0, x1, y1);
        }
        else
        {
            var bounds = VideoBounds();
            var output = _model.Project.Output;

            var dx = (int)Math.Round((point.X - _dragOrigin.X) / Math.Max(1, bounds.Width) * output.Width);
            var dy = (int)Math.Round((point.Y - _dragOrigin.Y) / Math.Max(1, bounds.Height) * output.Height);

            _model.SetPendingRect(_dragStartRect with { X = _dragStartRect.X + dx, Y = _dragStartRect.Y + dy });
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        _drawingNew = false;
        ReleaseMouseCapture();
        e.Handled = true;
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
