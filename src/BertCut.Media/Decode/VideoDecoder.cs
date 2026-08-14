using BertCut.Core.Media;
using FFmpeg.AutoGen.Abstractions;

namespace BertCut.Media.Decode;

/// <summary>A decoded frame as tightly-packed BGRA, ready for a WPF bitmap.</summary>
public sealed class DecodedFrame(int width, int height)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride { get; } = width * 4;

    public byte[] Pixels { get; } = new byte[width * height * 4];

    /// <summary>Index of this frame in its source, or -1 when nothing is loaded.</summary>
    public long FrameIndex { get; internal set; } = -1;
}

/// <summary>
/// Decodes single frames from one source file, addressed by exact frame index.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe. Each decode thread owns its own instance; the playback engine keeps
/// two so it can pre-roll the next clip across a cut boundary while the current one is
/// still playing.
/// </para>
/// <para>
/// Frames are addressed by index rather than by time. The <see cref="SourceIndex"/> maps
/// an index to the exact presentation timestamp the container holds, which is what keeps
/// this correct for the variable-frame-rate files screen recorders produce.
/// </para>
/// </remarks>
public sealed unsafe class VideoDecoder : IDisposable
{
    private readonly SourceIndex _index;
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _scaler;
    private byte_ptr4 _rgbData;
    private int4 _rgbLines;
    private int _streamIndex = -1;
    private bool _disposed;

    // sws_scale takes managed plane arrays. They are allocated once and refilled per
    // frame rather than per call, so decoding does not churn the heap at 60 Hz.
    private readonly byte*[] _sourcePlanes = new byte*[4];
    private readonly int[] _sourceStrides = new int[4];
    private readonly byte*[] _targetPlanes = new byte*[4];
    private readonly int[] _targetStrides = new int[4];

    /// <summary>Index of the frame currently decoded, or -1.</summary>
    private long _position = -1;

    public VideoDecoder(string path, SourceIndex index, int outputWidth, int outputHeight)
    {
        _index = index;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;

        try
        {
            Open(path);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int OutputWidth { get; }

    public int OutputHeight { get; }

    /// <summary>Source dimensions, before scaling to the project output size.</summary>
    public int SourceWidth { get; private set; }

    public int SourceHeight { get; private set; }

    private void Open(string path)
    {
        AVFormatContext* format = null;
        FfmpegLoader.Check(ffmpeg.avformat_open_input(&format, path, null, null), $"Opening '{path}'");
        _format = format;

        FfmpegLoader.Check(ffmpeg.avformat_find_stream_info(_format, null), "Reading stream info");

        AVCodec* codec = null;
        _streamIndex = FfmpegLoader.Check(
            ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0),
            "Finding a video stream");

        _codec = ffmpeg.avcodec_alloc_context3(codec);
        if (_codec is null) throw new FfmpegDecodeException("Could not allocate a decoder context.");

        FfmpegLoader.Check(
            ffmpeg.avcodec_parameters_to_context(_codec, _format->streams[_streamIndex]->codecpar),
            "Configuring the decoder");

        // Frame-level threading reorders output relative to input, which complicates the
        // decode-and-discard seek below for no benefit at these resolutions.
        _codec->thread_count = Math.Min(4, Environment.ProcessorCount);
        _codec->thread_type = ffmpeg.FF_THREAD_SLICE;

        FfmpegLoader.Check(ffmpeg.avcodec_open2(_codec, codec, null), "Opening the decoder");

        SourceWidth = _codec->width;
        SourceHeight = _codec->height;

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        // BGRA because that is what WPF's Bgra32 WriteableBitmap expects, so the decoded
        // buffer can be copied into the back buffer without a per-pixel pass.
        _scaler = ffmpeg.sws_getContext(
            SourceWidth, SourceHeight, _codec->pix_fmt,
            OutputWidth, OutputHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);

        if (_scaler is null) throw new FfmpegDecodeException("Could not create a scaler context.");

        var bufferSize = ffmpeg.av_image_get_buffer_size(
            AVPixelFormat.AV_PIX_FMT_BGRA, OutputWidth, OutputHeight, 1);

        var buffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
        _rgbData = default;
        _rgbLines = default;

        ffmpeg.av_image_fill_arrays(
            ref _rgbData, ref _rgbLines, buffer, AVPixelFormat.AV_PIX_FMT_BGRA,
            OutputWidth, OutputHeight, 1);

        // BGRA is a single interleaved plane, so only slot 0 is ever used.
        _targetPlanes[0] = _rgbData[0];
        _targetStrides[0] = _rgbLines[0];
    }

    /// <summary>
    /// Decodes the frame at <paramref name="frameIndex"/> into <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Advancing by one frame continues the current decode, which is the playback case and
    /// costs one packet. Any other position seeks to the preceding keyframe, flushes, and
    /// decodes forward discarding frames until the target — there is no shortcut for
    /// frame accuracy, which is why scrubbing shows filmstrip thumbnails until the drag
    /// settles.
    /// </remarks>
    public bool TryDecodeFrame(long frameIndex, DecodedFrame target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frameIndex < 0 || frameIndex >= _index.FrameCount) return false;

        if (_position == frameIndex && target.FrameIndex == frameIndex) return true;

        var sequential = _position >= 0 && frameIndex == _position + 1;
        if (!sequential) SeekToKeyframeBefore(frameIndex);

        var targetPts = _index.PtsOf(frameIndex);

        while (true)
        {
            if (!TryReadDecodedFrame(out var pts)) return false;

            _position = _index.FrameOf(pts);

            if (pts >= targetPts)
            {
                Convert(target, _position);
                return true;
            }
        }
    }

    /// <summary>Positions the decoder so the next decode starts before <paramref name="frameIndex"/>.</summary>
    private void SeekToKeyframeBefore(long frameIndex)
    {
        var keyframe = _index.KeyFrameAtOrBefore(frameIndex);
        var pts = _index.PtsOf(keyframe);

        FfmpegLoader.Check(
            ffmpeg.av_seek_frame(_format, _streamIndex, pts, ffmpeg.AVSEEK_FLAG_BACKWARD),
            "Seeking");

        // Mandatory after every seek. Without it the decoder emits frames from before the
        // seek and the discard loop above walks off the wrong reference.
        ffmpeg.avcodec_flush_buffers(_codec);
        _position = -1;
    }

    /// <summary>Pumps packets until the decoder yields a frame.</summary>
    private bool TryReadDecodedFrame(out long pts)
    {
        pts = 0;

        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_codec, _frame);

            if (received == 0)
            {
                pts = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                    ? _frame->best_effort_timestamp
                    : _frame->pts;
                return true;
            }

            if (received != ffmpeg.AVERROR(ffmpeg.EAGAIN) && received != ffmpeg.AVERROR_EOF)
                FfmpegLoader.Check(received, "Receiving a frame");

            if (received == ffmpeg.AVERROR_EOF) return false;

            if (!TrySendNextPacket()) return false;
        }
    }

    private bool TrySendNextPacket()
    {
        while (true)
        {
            ffmpeg.av_packet_unref(_packet);

            var read = ffmpeg.av_read_frame(_format, _packet);
            if (read == ffmpeg.AVERROR_EOF)
            {
                // Flush the decoder so frames still held in its reorder buffer come out.
                ffmpeg.avcodec_send_packet(_codec, null);
                return true;
            }

            if (read < 0) return false;
            if (_packet->stream_index != _streamIndex) continue;

            var sent = ffmpeg.avcodec_send_packet(_codec, _packet);
            if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                FfmpegLoader.Check(sent, "Sending a packet");

            return true;
        }
    }

    private void Convert(DecodedFrame target, long frameIndex)
    {
        for (uint i = 0; i < 4; i++)
        {
            _sourcePlanes[i] = _frame->data[i];
            _sourceStrides[i] = _frame->linesize[i];
        }

        ffmpeg.sws_scale(
            _scaler,
            _sourcePlanes, _sourceStrides, 0, SourceHeight,
            _targetPlanes, _targetStrides);

        var source = _rgbData[0];
        var sourceStride = _rgbLines[0];

        fixed (byte* destination = target.Pixels)
        {
            if (sourceStride == target.Stride)
            {
                Buffer.MemoryCopy(source, destination, target.Pixels.Length, (long)target.Stride * OutputHeight);
            }
            else
            {
                // sws pads rows for alignment; WriteableBitmap wants them packed.
                for (var y = 0; y < OutputHeight; y++)
                    Buffer.MemoryCopy(
                        source + ((long)y * sourceStride),
                        destination + ((long)y * target.Stride),
                        target.Stride,
                        target.Stride);
            }
        }

        target.FrameIndex = frameIndex;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_scaler is not null) ffmpeg.sws_freeContext(_scaler);
        if (_rgbData[0] is not null) ffmpeg.av_free(_rgbData[0]);

        if (_packet is not null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_frame is not null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_codec is not null) { var c = _codec; ffmpeg.avcodec_free_context(&c); _codec = null; }
        if (_format is not null) { var f = _format; ffmpeg.avformat_close_input(&f); _format = null; }
    }
}
