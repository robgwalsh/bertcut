using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BertCut.Core.Time;

namespace BertCut.App;

/// <summary>
/// The timeline strip: segments, crop and overlay spans, the marked range, and the playhead.
/// </summary>
/// <remarks>
/// <para>
/// Drawn directly in <see cref="OnRender"/> rather than composed from elements. The
/// content is a handful of rectangles that all change together whenever the document does,
/// so a visual tree per segment would cost layout passes for no benefit.
/// </para>
/// <para>
/// <b>No thumbnails.</b> The strip repaints on every playhead move, which during playback
/// is every frame; tiling decoded JPEGs across it made that repaint cost scale with the
/// width of the window, and it kept an ffmpeg pass over the whole file alive in the
/// background just to fill it. Segment fills and boundary seams already say where the cuts
/// are, which is what the strip is actually read for.
/// </para>
/// <para>
/// <b>Not focusable.</b> Nothing in the editing surface takes focus, because the moment a
/// control does, the single-letter shortcuts that make this editor fast stop reaching the
/// window's key handler.
/// </para>
/// </remarks>
public sealed class TimelineControl : FrameworkElement
{
    private static readonly Brush SegmentBrush = Frozen(Color.FromRgb(0x33, 0x3B, 0x47));
    private static readonly Brush SegmentAlternate = Frozen(Color.FromRgb(0x3C, 0x45, 0x52));
    private static readonly Brush SelectionBrush = Frozen(Color.FromArgb(0x55, 0x4F, 0x9C, 0xF5));
    private static readonly Brush CropBrush = Frozen(Color.FromArgb(0xAA, 0xF2, 0xA1, 0x4C));
    private static readonly Brush OverlayBrush = Frozen(Color.FromArgb(0xAA, 0x6D, 0xD4, 0x8B));
    private static readonly Brush BackgroundBrush = Frozen(Color.FromRgb(0x1E, 0x22, 0x28));
    private static readonly Pen BoundaryPen = FrozenPen(Color.FromRgb(0x11, 0x14, 0x18), 1);
    private static readonly Pen PlayheadPen = FrozenPen(Color.FromRgb(0xFF, 0x5C, 0x5C), 2);
    private static readonly Pen MarkPen = FrozenPen(Color.FromRgb(0x4F, 0x9C, 0xF5), 1.5);

    private EditorViewModel? _model;
    private bool _dragging;

    public TimelineControl()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    public void Bind(EditorViewModel model)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;

        _model = model;
        _model.PropertyChanged += OnModelChanged;
        InvalidateVisual();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        if (_model is null || !_model.HasMedia || _model.DurationFrames <= 0) return;

        var duration = _model.DurationFrames;
        double X(long frame) => frame / (double)duration * width;

        // Alternating segment fills make each ripple delete visible as a seam, which is
        // the fastest way to confirm a cut landed where it was meant to.
        var trackTop = 18.0;
        var trackHeight = Math.Max(12, height - 36);

        var i = 0;
        foreach (var segment in _model.Project.Base)
        {
            var x0 = X(segment.TimelineStart);
            var x1 = X(segment.TimelineStart + segment.LengthFrames);

            dc.DrawRectangle(
                i++ % 2 == 0 ? SegmentBrush : SegmentAlternate, null,
                new Rect(x0, trackTop, Math.Max(1, x1 - x0), trackHeight));
        }

        // A hard seam on top of the fills, so a cut reads even where two adjacent segments
        // happen to land on the same alternating colour.
        foreach (var segment in _model.Project.Base)
        {
            if (segment.TimelineStart == 0) continue;
            var x = X(segment.TimelineStart);
            dc.DrawLine(BoundaryPen, new Point(x, trackTop), new Point(x, trackTop + trackHeight));
        }

        // Crop and overlay spans ride as thin bands so they read as modifiers of the base
        // track rather than as separate clips.
        foreach (var crop in _model.Project.Crops)
            dc.DrawRectangle(CropBrush, null,
                new Rect(X(crop.Range.Start), trackTop, Math.Max(1, X(crop.Range.End) - X(crop.Range.Start)), 5));

        foreach (var overlay in _model.Project.Overlays)
            dc.DrawRectangle(OverlayBrush, null,
                new Rect(X(overlay.Range.Start), trackTop + trackHeight - 5,
                    Math.Max(1, X(overlay.Range.End) - X(overlay.Range.Start)), 5));

        if (_model.SelectedRange is { } selection)
        {
            var x0 = X(selection.Start);
            var x1 = X(selection.End);
            dc.DrawRectangle(SelectionBrush, null, new Rect(x0, trackTop, Math.Max(1, x1 - x0), trackHeight));
            dc.DrawLine(MarkPen, new Point(x0, 0), new Point(x0, height));
            dc.DrawLine(MarkPen, new Point(x1, 0), new Point(x1, height));
        }

        var playheadX = X(_model.Playhead);
        dc.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, height));
        dc.DrawEllipse(PlayheadPen.Brush, null, new Point(playheadX, 6), 4, 4);
    }

    // Dragging the timeline scrubs. Capture so a drag that leaves the control keeps
    // working, which is what makes fast back-and-forth scrubbing feel solid.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_model is null || !_model.HasMedia) return;

        _dragging = true;
        CaptureMouse();
        ScrubToMouse(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) ScrubToMouse(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ScrubToMouse(double x)
    {
        if (_model is null || _model.DurationFrames <= 0) return;

        var fraction = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1);
        _model.ScrubTo((long)(fraction * (_model.DurationFrames - 1)));
    }

    /// <summary>
    /// The strip was 96px tall to give thumbnails somewhere to live. What is left is a
    /// band, two 5px ribbons and a playhead, and giving that a third of the window's
    /// vertical budget only takes it away from the picture.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, 60);

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
