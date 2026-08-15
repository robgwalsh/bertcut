using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Core.Timeline;

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
/// <b>The waveform is cached geometry</b>, for the same reason. It is built once per pixel
/// column from the source's envelope and then frozen, and rebuilt only when the document,
/// the envelope or the width changes — never when the playhead moves. Walking the timeline
/// per column on every frame of playback would repeat the mistake the thumbnails made.
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
    private static readonly Brush WaveBrush = Frozen(Color.FromRgb(0x6E, 0x86, 0xA8));
    private static readonly Brush WaveBackground = Frozen(Color.FromRgb(0x24, 0x29, 0x30));

    /// <summary>Lane the waveform is drawn in, and the gap above it.</summary>
    private const double WaveHeight = 30;
    private const double WaveGap = 4;

    private EditorViewModel? _model;
    private bool _dragging;

    private Geometry? _waveform;
    private double _waveformWidth = -1;

    public TimelineControl()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    public void Bind(EditorViewModel model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.PeaksChanged -= OnPeaksChanged;
        }

        _model = model;
        _model.PropertyChanged += OnModelChanged;
        _model.PeaksChanged += OnPeaksChanged;

        _waveform = null;
        InvalidateVisual();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only a document change can alter which source frame a column shows. The playhead
        // moving repaints, as it must, but must not throw the geometry away — that is the
        // whole point of caching it.
        if (e.PropertyName is nameof(EditorViewModel.Project)) _waveform = null;

        InvalidateVisual();
    }

    private void OnPeaksChanged()
    {
        _waveform = null;
        InvalidateVisual();
    }

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
        var trackTop = 14.0;
        var waveTop = height - 12 - WaveHeight;
        var trackHeight = Math.Max(12, waveTop - WaveGap - trackTop);

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

        DrawWaveform(dc, width, waveTop);

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

    private void DrawWaveform(DrawingContext dc, double width, double top)
    {
        dc.DrawRectangle(WaveBackground, null, new Rect(0, top, width, WaveHeight));

        if (_waveform is null || _waveformWidth != width)
        {
            _waveform = BuildWaveform(width, top);
            _waveformWidth = width;
        }

        if (_waveform is not null) dc.DrawGeometry(WaveBrush, null, _waveform);
    }

    /// <summary>
    /// Builds the waveform for the <em>edited</em> timeline, one pixel column at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each column is resolved through a <see cref="TimelineResolver"/> exactly as the
    /// preview compositor resolves a frame, so the waveform shows what will be heard rather
    /// than what is in the file: a ripple delete removes its audio from the picture too, and
    /// an appended clip contributes its own. Anything less would make the strip disagree with
    /// itself the first time something was cut.
    /// </para>
    /// <para>
    /// Normalised against the loudest column so a quiet recording is still legible, with a
    /// floor so that near-silence is not amplified into noise that looks like content.
    /// </para>
    /// </remarks>
    private Geometry? BuildWaveform(double width, double top)
    {
        if (_model is null || !_model.HasMedia) return null;

        var project = _model.Project;
        var duration = project.DurationFrames;
        var columns = (int)Math.Floor(width);

        if (duration <= 0 || columns < 2) return null;

        var resolver = new TimelineResolver(project);
        var lows = new double[columns];
        var highs = new double[columns];
        var loudest = 0.0;
        var any = false;

        for (var x = 0; x < columns; x++)
        {
            var frame = (long)((double)x / columns * duration);
            if (resolver.Resolve(frame) is not { } resolution) continue;

            var peaks = _model.PeaksFor(resolution.SourceId);
            if (peaks is null || peaks.Count == 0) continue;

            var index = _model.IndexOf(resolution.SourceId);
            var sourceFrame = Math.Clamp(resolution.SourceFrame, 0, index.FrameCount - 1);
            var bucket = peaks.BucketOf(index.SecondsOf(sourceFrame));

            lows[x] = peaks.Min[bucket];
            highs[x] = peaks.Max[bucket];

            loudest = Math.Max(loudest, Math.Max(Math.Abs(lows[x]), Math.Abs(highs[x])));
            any = true;
        }

        if (!any) return null;

        var scale = WaveHeight / 2 / Math.Max(0.05, loudest);
        var centre = top + (WaveHeight / 2);

        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(0, centre - (highs[0] * scale)), isFilled: true, isClosed: true);

            // Down the tops left to right, back along the bottoms — one closed figure, so
            // this is a single filled shape rather than a line per column.
            for (var x = 1; x < columns; x++)
                context.LineTo(new Point(x, centre - (highs[x] * scale)), isStroked: false, isSmoothJoin: false);

            for (var x = columns - 1; x >= 0; x--)
                context.LineTo(new Point(x, centre - (lows[x] * scale)), isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
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
    /// The strip was 96px tall to give thumbnails somewhere to live, then 60 without them.
    /// The waveform lane earns back some of that: it is the only place in the window where
    /// an audio sync can be seen to have landed.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, 86);

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
