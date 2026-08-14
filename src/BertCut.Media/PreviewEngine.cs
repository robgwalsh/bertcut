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
/// <c>WriteableBitmap</c> can take directly; at the resolutions this editor targets
/// (typically 1280x768) that costs around a millisecond a frame, which is comfortably
/// inside a 60 Hz budget for a single stream.
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
    private readonly Dictionary<int, VideoDecoder> _decoders = [];
    private readonly Dictionary<int, DecodedFrame> _buffers = [];
    private readonly Func<int, SourceIndex> _indexOf;
    private readonly Func<int, string> _pathOf;
    private OutputFormat _output;
    private bool _disposed;

    public PreviewEngine(OutputFormat output, Func<int, SourceIndex> indexOf, Func<int, string> pathOf)
    {
        _output = output;
        _indexOf = indexOf;
        _pathOf = pathOf;
        Canvas = new DecodedFrame(output.Width, output.Height);
    }

    /// <summary>The composited output frame.</summary>
    public DecodedFrame Canvas { get; private set; }

    /// <summary>True when <see cref="Canvas"/> holds a rendered frame.</summary>
    public bool HasFrame { get; private set; }

    /// <summary>
    /// Composites the timeline frame at <paramref name="timelineFrame"/> into <see cref="Canvas"/>.
    /// </summary>
    public bool Render(TimelineResolver resolver, long timelineFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (resolver.Resolve(timelineFrame) is not { } resolution)
        {
            HasFrame = false;
            return false;
        }

        // The base layer is decoded straight to the output size, so an uncropped frame
        // needs no compositing pass at all — the common case costs one decode and one copy.
        var scaled = resolution.Crop is null;
        var baseFrame = Decode(resolution.SourceId, resolution.SourceFrame, scaled);
        if (baseFrame is null)
        {
            HasFrame = false;
            return false;
        }

        if (resolution.Crop is { } crop)
            ZoomCropInto(baseFrame, crop, Canvas);
        else
            Array.Copy(baseFrame.Pixels, Canvas.Pixels, Canvas.Pixels.Length);

        if (resolution.Overlay is { } overlay)
        {
            var overlayFrame = Decode(overlay.SourceId, resolution.OverlaySourceFrame, scaled: false);
            if (overlayFrame is not null) BlitInto(overlayFrame, overlay.Dest, Canvas);
        }

        Canvas.FrameIndex = timelineFrame;
        HasFrame = true;
        return true;
    }

    /// <summary>Re-creates the canvas when the project's output size changes.</summary>
    public void SetOutput(OutputFormat output)
    {
        if (output.Width == _output.Width && output.Height == _output.Height)
        {
            _output = output;
            return;
        }

        _output = output;
        Canvas = new DecodedFrame(output.Width, output.Height);
        HasFrame = false;

        // Decoders scale on output, so they all have to be rebuilt at the new size.
        foreach (var decoder in _decoders.Values) decoder.Dispose();
        _decoders.Clear();
        _buffers.Clear();
    }

    /// <summary>Closes every open decoder, e.g. before the project's sources change.</summary>
    public void Reset()
    {
        foreach (var decoder in _decoders.Values) decoder.Dispose();
        _decoders.Clear();
        _buffers.Clear();
        HasFrame = false;
    }

    private DecodedFrame? Decode(int sourceId, long sourceFrame, bool scaled)
    {
        if (!_decoders.TryGetValue(sourceId, out var decoder))
        {
            var index = _indexOf(sourceId);

            // When a crop is in play the source is decoded at native size so the crop is
            // taken from real pixels rather than from an already-downscaled image.
            var (width, height) = scaled
                ? (_output.Width, _output.Height)
                : NativeSize(sourceId);

            decoder = new VideoDecoder(_pathOf(sourceId), index, width, height);
            _decoders[sourceId] = decoder;
            _buffers[sourceId] = new DecodedFrame(width, height);
        }

        var buffer = _buffers[sourceId];
        return decoder.TryDecodeFrame(sourceFrame, buffer) ? buffer : null;
    }

    private (int Width, int Height) NativeSize(int sourceId)
    {
        // Opened briefly to learn the source's real dimensions; the decoder created
        // afterwards keeps them.
        using var probe = new VideoDecoder(_pathOf(sourceId), _indexOf(sourceId), 16, 16);
        return (probe.SourceWidth, probe.SourceHeight);
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
    private static void BlitInto(DecodedFrame source, RectI dest, DecodedFrame canvas)
    {
        if (dest.W <= 0 || dest.H <= 0) return;

        var stepX = (double)source.Width / dest.W;
        var stepY = (double)source.Height / dest.H;

        var x0 = Math.Max(0, dest.X);
        var y0 = Math.Max(0, dest.Y);
        var x1 = Math.Min(canvas.Width, dest.X + dest.W);
        var y1 = Math.Min(canvas.Height, dest.Y + dest.H);

        for (var y = y0; y < y1; y++)
        {
            var sourceY = Math.Clamp((int)((y - dest.Y) * stepY), 0, source.Height - 1);
            var sourceRow = sourceY * source.Stride;
            var targetRow = y * canvas.Stride;

            for (var x = x0; x < x1; x++)
            {
                var sourceX = Math.Clamp((int)((x - dest.X) * stepX), 0, source.Width - 1);
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
