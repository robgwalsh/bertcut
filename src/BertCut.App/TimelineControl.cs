using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BertCut.Core.Input;
using BertCut.Core.Model;
using BertCut.Core.Time;
using BertCut.Core.Timeline;

namespace BertCut.App;

/// <summary>Which part of an overlay's band a pointer has hold of.</summary>
/// <remarks>
/// The whole vocabulary of dragging a clip: pull it in the middle and it moves, pull an end
/// and that end moves. Decided by <see cref="TimelineControl"/>, because it is a question
/// about pixels, and acted on by <see cref="EditorViewModel"/>, because the answer is an edit.
/// </remarks>
public enum OverlayGrip
{
    /// <summary>The body of the clip: dragging moves the whole thing.</summary>
    Body,

    /// <summary>Its leading edge: dragging trims the in-point.</summary>
    Start,

    /// <summary>Its trailing edge: dragging trims the out-point.</summary>
    End,
}

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
    private static readonly Brush SegmentSelected = Frozen(Color.FromRgb(0x50, 0x5E, 0x70));
    private static readonly Pen SegmentSelectedPen = FrozenPen(Color.FromRgb(0xD6, 0xE1, 0xEF), 1);
    private static readonly Brush RulerBrush = Frozen(Color.FromRgb(0x26, 0x2B, 0x33));
    private static readonly Brush SelectionBrush = Frozen(Color.FromArgb(0x55, 0x4F, 0x9C, 0xF5));
    private static readonly Brush CropBrush = Frozen(Color.FromArgb(0xAA, 0xF2, 0xA1, 0x4C));
    private static readonly Brush OverlayBrush = Frozen(Color.FromArgb(0xAA, 0x6D, 0xD4, 0x8B));
    private static readonly Brush OverlaySelectedBrush = Frozen(Color.FromRgb(0x9A, 0xEB, 0xB2));
    private static readonly Pen OverlaySelectedPen = FrozenPen(Color.FromRgb(0xF2, 0xFF, 0xF6), 1);
    private static readonly Brush OverlayPendingBrush = Frozen(Color.FromArgb(0x44, 0x6D, 0xD4, 0x8B));
    private static readonly Pen OverlayPendingPen = FrozenPen(Color.FromArgb(0x99, 0x9A, 0xEB, 0xB2), 1);
    private static readonly Brush GripBrush = Frozen(Color.FromRgb(0x22, 0x5F, 0x3B));
    private static readonly Brush BackgroundBrush = Frozen(Color.FromRgb(0x1E, 0x22, 0x28));
    private static readonly Pen BoundaryPen = FrozenPen(Color.FromRgb(0x11, 0x14, 0x18), 1);
    private static readonly Pen PlayheadPen = FrozenPen(Color.FromRgb(0xFF, 0x5C, 0x5C), 2);
    private static readonly Pen MarkPen = FrozenPen(Color.FromRgb(0x4F, 0x9C, 0xF5), 1.5);
    private static readonly Brush WaveBrush = Frozen(Color.FromRgb(0x6E, 0x86, 0xA8));
    private static readonly Brush WaveBackground = Frozen(Color.FromRgb(0x24, 0x29, 0x30));

    /// <summary>Lane the waveform is drawn in, and the gap above it.</summary>
    private const double WaveHeight = 30;
    private const double WaveGap = 4;

    /// <summary>
    /// Where the segment track starts, leaving a lane above it for the playhead's head.
    /// </summary>
    /// <remarks>
    /// That lane is the ruler, and since the track itself started answering to clicks it is
    /// also the place a drag always scrubs. Tinted rather than left as background for exactly
    /// that reason: a strip you can drag along has to look like one.
    /// </remarks>
    private const double TrackTop = 16;

    /// <summary>The crop band along the top of the track.</summary>
    private const double CropBandHeight = 5;

    /// <summary>
    /// The overlay band along the bottom of it, twice as tall as the crop's.
    /// </summary>
    /// <remarks>
    /// A crop is a thing to look at; an overlay is a thing to grab. Five pixels reads as a
    /// marking, which is all the crop is, but it is not a target anyone can hit reliably with
    /// a pointer — and a band you can drag has to look like one.
    /// </remarks>
    private const double OverlayBandHeight = 10;

    /// <summary>How far outside the band a press still counts as landing on it.</summary>
    private const double GrabTolerance = 3;

    /// <summary>
    /// How much of each end of a band trims rather than moves.
    /// </summary>
    /// <remarks>
    /// Capped at a third of the band in <see cref="OverlayAt"/>, so a short clip always keeps
    /// a middle to take hold of. A clip you can only trim and never move would be the worst
    /// of the two.
    /// </remarks>
    private const double EdgeGrab = 5;

    /// <summary>So a very short overlay is still visible, and still catchable.</summary>
    private const double MinBandWidth = 3;

    /// <summary>What the pointer is doing between press and release.</summary>
    private enum DragKind
    {
        None,

        /// <summary>Scrubbing the playhead.</summary>
        Scrub,

        /// <summary>Moving or trimming the selected overlay.</summary>
        Overlay,

        /// <summary>Holding a base segment, which becomes a reorder once the pointer travels.</summary>
        Segment,
    }

    /// <summary>
    /// How far the pointer must travel before holding a segment starts rearranging the track.
    /// </summary>
    /// <remarks>
    /// Without it every click on the base track would be a potential reorder, and picking a
    /// segment out to look at it could not survive a hand that moves a pixel while pressing.
    /// </remarks>
    private const double DragThreshold = 4;

    private EditorViewModel? _model;
    private DragKind _drag;
    private double _pressX;
    private bool _reordering;

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

        double X(long frame) => XOf(frame, width);

        // Alternating segment fills make each ripple delete visible as a seam, which is
        // the fastest way to confirm a cut landed where it was meant to.
        var (trackTop, trackHeight) = Track();
        var waveTop = height - 12 - WaveHeight;

        dc.DrawRectangle(RulerBrush, null, new Rect(0, 0, width, TrackTop));

        var selectedSegment = _model.SelectedSegment;

        for (var i = 0; i < _model.Project.Base.Length; i++)
        {
            var band = SegmentRect(_model.Project.Base[i], width);

            dc.DrawRectangle(
                i == selectedSegment ? SegmentSelected : i % 2 == 0 ? SegmentBrush : SegmentAlternate,
                i == selectedSegment ? SegmentSelectedPen : null,
                band);
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
                new Rect(X(crop.Range.Start), trackTop,
                    Math.Max(1, X(crop.Range.End) - X(crop.Range.Start)), CropBandHeight));

        // The selected one is filled brighter and outlined, so which clip a drag is about to
        // move is unambiguous even where two of them sit end to end.
        var selected = _model.SelectedOverlay;

        for (var o = 0; o < _model.Project.Overlays.Length; o++)
        {
            var band = OverlayBandRect(_model.Project.Overlays[o].Range, width);

            if (o != selected)
            {
                dc.DrawRectangle(OverlayBrush, null, band);
                continue;
            }

            dc.DrawRectangle(OverlaySelectedBrush, OverlaySelectedPen, band);

            // Darker ends, where pulling trims instead of moving. A clip too narrow to show
            // them is also too narrow to trim by hand, and the drawing says so.
            var grip = EdgeWidth(band);
            if (grip < EdgeGrab) continue;

            dc.DrawRectangle(GripBrush, null, new Rect(band.X, band.Y, grip, band.Height));
            dc.DrawRectangle(GripBrush, null, new Rect(band.Right - grip, band.Y, grip, band.Height));
        }

        // An overlay being positioned has no clip on the strip yet, and without marks it has
        // no marked range either — so the span Enter would commit is drawn in its own lane,
        // faint and outlined. Faint because it is a proposal; in the overlay lane and not
        // somewhere of its own because where it lands is the whole question. Painted over the
        // committed bands, since it truncates whatever it overlaps when it is placed.
        if (_model.Mode == EditorMode.Overlay)
            dc.DrawRectangle(
                OverlayPendingBrush, OverlayPendingPen, OverlayBandRect(_model.PendingRange, width));

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

    // ---- geometry ------------------------------------------------------------------
    //
    // Shared by the render pass and the hit test, so what the user aims at is by
    // construction what was painted. Two copies of this arithmetic would be one band drawn
    // and a different one grabbed, and the drift would only show at some window widths.

    /// <summary>Top and height of the segment track.</summary>
    private (double Top, double Height) Track()
    {
        var waveTop = ActualHeight - 12 - WaveHeight;
        return (TrackTop, Math.Max(12, waveTop - WaveGap - TrackTop));
    }

    private double XOf(long frame, double width) =>
        _model is null || _model.DurationFrames <= 0 ? 0 : frame / (double)_model.DurationFrames * width;

    /// <summary>Where a base segment is painted, in this element's coordinates.</summary>
    private Rect SegmentRect(BaseSegment segment, double width)
    {
        var (top, height) = Track();
        var x0 = XOf(segment.TimelineStart, width);
        var x1 = XOf(segment.TimelineStart + segment.LengthFrames, width);

        return new Rect(x0, top, Math.Max(1, x1 - x0), height);
    }

    /// <summary>Where an overlay's band is painted, in this element's coordinates.</summary>
    private Rect OverlayBandRect(FrameRange range, double width)
    {
        var (top, height) = Track();
        var x0 = XOf(range.Start, width);
        var x1 = XOf(range.End, width);

        return new Rect(
            x0, top + height - OverlayBandHeight, Math.Max(MinBandWidth, x1 - x0), OverlayBandHeight);
    }

    /// <summary>How much of each end of a band trims rather than moves.</summary>
    private static double EdgeWidth(Rect band) => Math.Min(EdgeGrab, band.Width / 3);

    /// <summary>
    /// Which overlay's band is under a point, and what part of it.
    /// </summary>
    /// <remarks>
    /// Two clips that touch share a pixel column, and both of them answer to a press on it.
    /// The selected one wins that tie: the user has already pointed at it, and the
    /// alternative is that the front of a clip can never be trimmed once something abuts it —
    /// the neighbour's out-point would take every press on the seam. Otherwise the first
    /// band along wins, which is only reached when nothing is selected.
    /// </remarks>
    private (int Index, OverlayGrip Grip)? OverlayAt(Point point)
    {
        if (_model is null || !_model.HasMedia || _model.DurationFrames <= 0) return null;

        var width = ActualWidth;
        (int Index, OverlayGrip Grip)? first = null;

        for (var i = 0; i < _model.Project.Overlays.Length; i++)
        {
            if (GripAt(_model.Project.Overlays[i].Range, point, width) is not { } grip) continue;
            if (i == _model.SelectedOverlay) return (i, grip);

            first ??= (i, grip);
        }

        return first;
    }

    /// <summary>Which base segment is under a point, or null when it is off the track.</summary>
    /// <remarks>
    /// The selected one wins a shared boundary column, on the same reasoning as the overlay
    /// bands: a drag that starts on a seam should go on being about the clip already in hand.
    /// </remarks>
    private int? SegmentAt(Point point)
    {
        if (_model is null || !_model.HasMedia || _model.DurationFrames <= 0) return null;

        var width = ActualWidth;
        int? first = null;

        for (var i = 0; i < _model.Project.Base.Length; i++)
        {
            if (!SegmentRect(_model.Project.Base[i], width).Contains(point)) continue;
            if (i == _model.SelectedSegment) return i;

            first ??= i;
        }

        return first;
    }

    /// <summary>Where on one band a point falls, or null when it is not on it at all.</summary>
    private OverlayGrip? GripAt(FrameRange range, Point point, double width)
    {
        var band = OverlayBandRect(range, width);

        var reach = band;
        reach.Inflate(GrabTolerance, GrabTolerance);
        if (!reach.Contains(point)) return null;

        var edge = EdgeWidth(band);

        if (point.X <= band.X + edge) return OverlayGrip.Start;
        if (point.X >= band.Right - edge) return OverlayGrip.End;

        return OverlayGrip.Body;
    }

    /// <summary>The timeline frame at a horizontal position.</summary>
    private long FrameAt(double x)
    {
        if (_model is null || _model.DurationFrames <= 0) return 0;

        var fraction = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1);
        return (long)(fraction * _model.DurationFrames);
    }

    // ---- the pointer ---------------------------------------------------------------
    //
    // The gesture proper lives in the three methods below rather than in the event
    // handlers, because the harness cannot synthesise a pointer: there is no cursor over an
    // offscreen window, and moving the real one would move the user's. Driving these instead
    // leaves the hit test, the grab offset and the pixel-to-frame arithmetic under test
    // rather than stepped around.

    /// <summary>Begins whatever gesture a press at this point means.</summary>
    internal void PointerDown(Point point)
    {
        if (_model is null || !_model.HasMedia) return;

        _pressX = point.X;
        _reordering = false;

        // An overlay band takes the press first: it is the smaller and more deliberate
        // target, and it sits inside the track rather than beside it.
        if (OverlayAt(point) is { } hit && _model.BeginOverlayDrag(hit.Index, FrameAt(point.X), hit.Grip))
        {
            _drag = DragKind.Overlay;
            return;
        }

        // On the track: pick out the segment, and leave the playhead alone. Rearranging the
        // running order waits for the pointer to travel — see PointerMove.
        if (SegmentAt(point) is { } segment)
        {
            _drag = DragKind.Segment;
            _model.SelectSegment(segment);
            return;
        }

        // Off the track — the ruler, the waveform, the margins. Scrubs, and lets go of
        // whatever was selected: clicking off a thing is how a selection is dropped.
        _model.ClearOverlaySelection();
        _model.ClearSegmentSelection();
        _drag = DragKind.Scrub;

        // Before the first move, because that is what stops the transport: the rate to come
        // back to is the one the press found, not the zero the scrub itself leaves behind.
        _model.BeginScrub();
        ScrubToMouse(point.X);
    }

    internal void PointerMove(Point point)
    {
        switch (_drag)
        {
            case DragKind.Scrub:
                ScrubToMouse(point.X);
                break;

            case DragKind.Overlay:
                _model?.DragOverlayTo(FrameAt(point.X));
                break;

            case DragKind.Segment:
                if (!_reordering)
                {
                    if (Math.Abs(point.X - _pressX) < DragThreshold) return;

                    // Nothing to reorder — one segment, or a placement in progress. The drag
                    // ends there rather than falling back to scrubbing: the track does not
                    // move the playhead, and a lane that made an exception when it happened
                    // to have nothing to rearrange would be worse than one that never does.
                    if (_model?.BeginSegmentReorder() != true)
                    {
                        _drag = DragKind.None;
                        return;
                    }

                    _reordering = true;
                }

                _model?.DragSegmentTo(FrameAt(point.X));
                break;
        }
    }

    internal void PointerUp()
    {
        if (_drag == DragKind.Overlay) _model?.EndOverlayDrag();
        if (_drag == DragKind.Segment) _model?.EndSegmentDrag();
        if (_drag == DragKind.Scrub) _model?.EndScrub();

        _drag = DragKind.None;
        _reordering = false;
    }

    /// <summary>The middle of a frame's overlay band, for a harness driving a drag.</summary>
    internal Point OverlayBandPoint(long frame)
    {
        var (top, height) = Track();
        return new Point(XOf(frame, ActualWidth), top + height - (OverlayBandHeight / 2));
    }

    /// <summary>A frame's segment, clear of the overlay band below it — likewise.</summary>
    internal Point SegmentPoint(long frame)
    {
        var (top, height) = Track();
        return new Point(XOf(frame, ActualWidth), top + ((height - OverlayBandHeight) / 2));
    }

    /// <summary>The ruler above the track, where a press only ever seeks.</summary>
    internal Point RulerPoint(long frame) => new(XOf(frame, ActualWidth), TrackTop / 2);

    // Capture so a drag that leaves the control keeps working, which is what makes fast
    // back-and-forth scrubbing feel solid — and what stops an overlay being dropped
    // somewhere unintended the moment the pointer strays out of an 86px strip.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_model is null || !_model.HasMedia) return;

        CaptureMouse();
        PointerDown(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_model is null || !_model.HasMedia) return;

        var point = e.GetPosition(this);

        // Idle, the cursor is the only advertisement that these bands can be dragged at all
        // — and the only thing that says the ends do something different from the middle.
        if (_drag == DragKind.None)
        {
            Cursor = OverlayAt(point)?.Grip switch
            {
                OverlayGrip.Body => Cursors.Hand,
                OverlayGrip.Start or OverlayGrip.End => Cursors.SizeWE,
                _ => SegmentAt(point) is not null ? Cursors.Hand : Cursors.Arrow,
            };

            return;
        }

        PointerMove(point);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == DragKind.None) return;

        PointerUp();
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>Ends a drag that lost the mouse, e.g. to a window that stole capture.</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        PointerUp();
        base.OnLostMouseCapture(e);
    }

    private void ScrubToMouse(double x)
    {
        if (_model is null || _model.DurationFrames <= 0) return;

        // One short of the end, because the playhead sits on a frame where the position a
        // drag reads is the boundary in front of one.
        _model.ScrubTo(Math.Min(FrameAt(x), _model.DurationFrames - 1));
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
