using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Core.Timeline;
using BertCut.Media.Decode;

namespace BertCut.Media;

/// <summary>
/// Renders the timeline's current frame by decoding the right source frame and applying
/// crop and overlay.
/// </summary>
/// <remarks>
/// <para>
/// This is the software path. It composites into a BGRA buffer that a
/// <c>WriteableBitmap</c> can take directly.
/// </para>
/// <para>
/// <b>Synchronous, and owned by one thread at a time.</b> The decoders under it are not
/// thread-safe and neither is this. <see cref="PreviewPump"/> is what gives it a thread of
/// its own in the app; tests drive it directly, on theirs.
/// </para>
/// <para>
/// The geometry applied here comes from <see cref="TimelineResolver"/> — the same source
/// the ffmpeg argument builder derives from — so the preview and the exported file agree
/// by construction rather than by two implementations happening to match.
/// </para>
/// <para>
/// Decoders are cached per source. Opening one costs a file open and a codec init, which
/// is far too expensive to repeat per frame when an overlay brings a second source in and
/// out of view.
/// </para>
/// </remarks>
public sealed class PreviewEngine : IDisposable
{
    /// <summary>
    /// Cached decoders, keyed by source <em>and by the size they scale to</em>.
    /// </summary>
    /// <remarks>
    /// A cropped frame is decoded at the source's native size so the zoom is taken from real
    /// pixels; everything else is decoded straight to the render size. One source can need
    /// both — a timeline cropped in places and not in others — and a single cache slot would
    /// hand the second caller a buffer of the wrong dimensions.
    /// </remarks>
    private readonly Dictionary<(int Source, bool Native), VideoDecoder> _decoders = [];
    private readonly Dictionary<(int Source, bool Native), DecodedFrame> _buffers = [];

    private readonly Func<int, SourceIndex> _indexOf;
    private readonly Func<int, string> _pathOf;
    private OutputFormat _output;
    private bool _disposed;

    public PreviewEngine(OutputFormat output, Func<int, SourceIndex> indexOf, Func<int, string> pathOf)
    {
        ArgumentNullException.ThrowIfNull(output);

        _output = output;
        _indexOf = indexOf;
        _pathOf = pathOf;

        RenderWidth = output.Width;
        RenderHeight = output.Height;
        Canvas = new DecodedFrame(RenderWidth, RenderHeight);
    }

    /// <summary>The composited output frame.</summary>
    public DecodedFrame Canvas { get; private set; }

    /// <summary>True when <see cref="Canvas"/> holds a rendered frame.</summary>
    public bool HasFrame { get; private set; }

    /// <summary>
    /// The size frames are composited at, which defaults to the output size.
    /// </summary>
    /// <remarks>
    /// Separate from the output format because the preview only ever has to be as detailed
    /// as the area it is displayed in, and every pixel above that is scaled, copied and
    /// uploaded once a frame for nothing. Geometry is unaffected: crop and overlay
    /// rectangles are in output space and are mapped through on the way in, so a preview
    /// rendered at half size shows the same composition, smaller.
    /// </remarks>
    public int RenderWidth { get; private set; }

    public int RenderHeight { get; private set; }

    /// <summary>A buffer this engine can render into, at the current render size.</summary>
    public DecodedFrame NewFrame() => new(RenderWidth, RenderHeight);

    /// <summary>
    /// Composites the timeline frame at <paramref name="timelineFrame"/> into <see cref="Canvas"/>.
    /// </summary>
    public bool Render(TimelineResolver resolver, long timelineFrame)
    {
        HasFrame = Render(resolver, timelineFrame, Canvas);
        return HasFrame;
    }

    /// <summary>
    /// Composites the timeline frame at <paramref name="timelineFrame"/> into
    /// <paramref name="target"/>, which must be at the current render size.
    /// </summary>
    /// <remarks>
    /// Rendering into a caller-supplied buffer is what lets the pump keep several frames
    /// alive at once: one being displayed, one being filled, and the rest read ahead.
    /// </remarks>
    public bool Render(TimelineResolver resolver, long timelineFrame, DecodedFrame target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(target);

        if (target.Width != RenderWidth || target.Height != RenderHeight)
            throw new ArgumentException(
                $"The target is {target.Width}x{target.Height}; this engine renders {RenderWidth}x{RenderHeight}.",
                nameof(target));

        if (resolver.Resolve(timelineFrame) is not { } resolution) return false;

        var project = resolver.Project;

        // The overwhelmingly common frame: no crop, no overlay. The decoder scales straight
        // into the caller's buffer, so nothing is composited and nothing is copied.
        if (resolution is { Crop: null, Overlay: null })
        {
            var decoder = DecoderFor(project, resolution.SourceId, native: false);
            if (!decoder.TryDecodeFrame(resolution.SourceFrame, target)) return false;

            target.FrameIndex = timelineFrame;
            return true;
        }

        // A crop reads from native pixels; an overlay's base does not, because the base is
        // still shown whole.
        var native = resolution.Crop is not null;
        var baseFrame = Decode(project, resolution.SourceId, resolution.SourceFrame, native);
        if (baseFrame is null) return false;

        if (resolution.Crop is { } crop) ZoomCropInto(baseFrame, crop, target);
        else Array.Copy(baseFrame.Pixels, target.Pixels, target.Pixels.Length);

        if (resolution.Overlay is { } overlay)
        {
            var overlayFrame = Decode(project, overlay.SourceId, resolution.OverlaySourceFrame, native: true);
            if (overlayFrame is not null) BlitInto(overlayFrame, overlay.Dest, target, _output);
        }

        target.FrameIndex = timelineFrame;
        return true;
    }

    /// <summary>Re-creates the canvas when the project's output size changes.</summary>
    /// <remarks>
    /// A render size set by the caller survives an output whose dimensions did not change,
    /// which is every ordinary edit — rebuilding every decoder on each keystroke would cost
    /// far more than this saves.
    /// </remarks>
    public void SetOutput(OutputFormat output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var sameSize = output.Width == _output.Width && output.Height == _output.Height;
        _output = output;

        if (!sameSize) Resize(output.Width, output.Height);
    }

    /// <summary>
    /// Composites at <paramref name="width"/> x <paramref name="height"/> instead of at the
    /// output size.
    /// </summary>
    /// <remarks>
    /// Every cached decoder scales on output, so all of them are rebuilt. Callers are
    /// expected to snap to a few stable sizes rather than track a window edge continuously.
    /// </remarks>
    public void SetRenderSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        if (width == RenderWidth && height == RenderHeight) return;

        Resize(width, height);
    }

    /// <summary>Closes every open decoder, e.g. before the project's sources change.</summary>
    public void Reset()
    {
        foreach (var decoder in _decoders.Values) decoder.Dispose();
        _decoders.Clear();
        _buffers.Clear();
        HasFrame = false;
    }

    private void Resize(int width, int height)
    {
        RenderWidth = width;
        RenderHeight = height;
        Canvas = new DecodedFrame(width, height);

        Reset();
    }

    private DecodedFrame? Decode(Project project, int sourceId, long sourceFrame, bool native)
    {
        var decoder = DecoderFor(project, sourceId, native);
        var buffer = _buffers[(sourceId, native)];

        return decoder.TryDecodeFrame(sourceFrame, buffer) ? buffer : null;
    }

    private VideoDecoder DecoderFor(Project project, int sourceId, bool native)
    {
        var key = (sourceId, native);
        if (_decoders.TryGetValue(key, out var existing)) return existing;

        // The source's own dimensions come off the document, which already carries them from
        // the probe. Opening a second decoder just to read them back cost a file open, a
        // codec init and a scaler context every time a crop or an overlay came into view.
        var media = project.RequireSource(sourceId);

        var (width, height) = native
            ? (media.Width, media.Height)
            : (RenderWidth, RenderHeight);

        var decoder = new VideoDecoder(_pathOf(sourceId), _indexOf(sourceId), width, height);
        _decoders[key] = decoder;
        _buffers[key] = new DecodedFrame(width, height);

        return decoder;
    }

    /// <summary>
    /// Scales a crop rectangle up to fill the canvas.
    /// </summary>
    /// <remarks>
    /// Nearest-neighbour, because this runs per preview frame and the export does the
    /// same operation with a proper Lanczos filter. The preview is slightly softer than
    /// the exported result in this one respect, which is the right trade for a preview
    /// that has to keep up with playback.
    /// </remarks>
    private static void ZoomCropInto(DecodedFrame source, RectI crop, DecodedFrame canvas)
    {
        // The crop is expressed in output-space pixels; map it onto the source's own grid.
        var scaleX = (double)source.Width / canvas.Width;
        var scaleY = (double)source.Height / canvas.Height;

        var sx0 = crop.X * scaleX;
        var sy0 = crop.Y * scaleY;
        var stepX = crop.W * scaleX / canvas.Width;
        var stepY = crop.H * scaleY / canvas.Height;

        for (var y = 0; y < canvas.Height; y++)
        {
            var sourceY = Math.Clamp((int)(sy0 + (y * stepY)), 0, source.Height - 1);
            var sourceRow = sourceY * source.Stride;
            var targetRow = y * canvas.Stride;

            for (var x = 0; x < canvas.Width; x++)
            {
                var sourceX = Math.Clamp((int)(sx0 + (x * stepX)), 0, source.Width - 1);
                var s = sourceRow + (sourceX * 4);
                var t = targetRow + (x * 4);

                canvas.Pixels[t] = source.Pixels[s];
                canvas.Pixels[t + 1] = source.Pixels[s + 1];
                canvas.Pixels[t + 2] = source.Pixels[s + 2];
                canvas.Pixels[t + 3] = 255;
            }
        }
    }

    /// <summary>Draws the overlay into its destination rectangle, scaling to fit.</summary>
    /// <remarks>
    /// The destination is in output-space pixels, so it is mapped onto the canvas rather
    /// than used directly — a preview rendered at half size puts the clip in the same place,
    /// half as large.
    /// </remarks>
    private static void BlitInto(DecodedFrame source, RectI dest, DecodedFrame canvas, OutputFormat output)
    {
        if (dest.W <= 0 || dest.H <= 0) return;

        var toCanvasX = (double)canvas.Width / output.Width;
        var toCanvasY = (double)canvas.Height / output.Height;

        var destX = dest.X * toCanvasX;
        var destY = dest.Y * toCanvasY;
        var destW = dest.W * toCanvasX;
        var destH = dest.H * toCanvasY;

        if (destW < 1 || destH < 1) return;

        var stepX = source.Width / destW;
        var stepY = source.Height / destH;

        var x0 = Math.Max(0, (int)destX);
        var y0 = Math.Max(0, (int)destY);
        var x1 = Math.Min(canvas.Width, (int)(destX + destW));
        var y1 = Math.Min(canvas.Height, (int)(destY + destH));

        for (var y = y0; y < y1; y++)
        {
            var sourceY = Math.Clamp((int)((y - destY) * stepY), 0, source.Height - 1);
            var sourceRow = sourceY * source.Stride;
            var targetRow = y * canvas.Stride;

            for (var x = x0; x < x1; x++)
            {
                var sourceX = Math.Clamp((int)((x - destX) * stepX), 0, source.Width - 1);
                var s = sourceRow + (sourceX * 4);
                var t = targetRow + (x * 4);

                canvas.Pixels[t] = source.Pixels[s];
                canvas.Pixels[t + 1] = source.Pixels[s + 1];
                canvas.Pixels[t + 2] = source.Pixels[s + 2];
                canvas.Pixels[t + 3] = 255;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }
}
