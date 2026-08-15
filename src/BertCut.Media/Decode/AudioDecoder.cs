using FFmpeg.AutoGen.Abstractions;

namespace BertCut.Media.Decode;

/// <summary>
/// Decodes one file's audio to interleaved float samples at a fixed rate and channel count.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="VideoDecoder"/>, and deliberately shaped differently.
/// Video is addressed by exact frame index because an editor cuts on frames; audio is
/// addressed by seconds and read as a stream, because nothing above this cares which packet
/// a sample came out of. Seconds come from <c>SourceIndex.SecondsOf</c> at every call site,
/// which is what keeps seeks correct on the variable-frame-rate files screen recorders
/// produce.
/// </para>
/// <para>
/// Everything is resampled to the project's rate and to stereo on the way out, so callers
/// mix and correlate without caring that one camera recorded 44.1 kHz mono and the other
/// 48 kHz stereo. That conversion is <c>swresample</c>'s job and it ships in
/// <c>tools/ffmpeg</c> already.
/// </para>
/// <para>
/// Not thread-safe. One instance per thread, like <see cref="VideoDecoder"/>.
/// </para>
/// </remarks>
public sealed unsafe class AudioDecoder : IDisposable
{
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwrContext* _resampler;
    private int _streamIndex = -1;
    private bool _disposed;
    private bool _drained;

    /// <summary>Converted samples decoded but not yet handed out, interleaved.</summary>
    private float[] _pending = [];
    private int _pendingStart;
    private int _pendingCount;

    /// <summary>Scratch for one <c>swr_convert</c> call.</summary>
    private float[] _converted = [];

    public AudioDecoder(string path, int sampleRate, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        SampleRate = sampleRate;
        Channels = channels;

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

    /// <summary>Output rate in samples per second per channel.</summary>
    public int SampleRate { get; }

    /// <summary>Output channel count.</summary>
    public int Channels { get; }

    /// <summary>Length of the audio stream in seconds, or 0 when the container omits it.</summary>
    public double DurationSeconds { get; private set; }

    /// <summary>
    /// Where the next sample <see cref="Read"/> returns sits in the source, in seconds.
    /// </summary>
    public double PositionSeconds { get; private set; }

    /// <summary>True when the file has an audio stream this can decode.</summary>
    public static bool HasAudioStream(string path)
    {
        AVFormatContext* format = null;

        if (ffmpeg.avformat_open_input(&format, path, null, null) < 0) return false;

        try
        {
            if (ffmpeg.avformat_find_stream_info(format, null) < 0) return false;
            return ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0) >= 0;
        }
        finally
        {
            ffmpeg.avformat_close_input(&format);
        }
    }

    private void Open(string path)
    {
        AVFormatContext* format = null;
        FfmpegLoader.Check(ffmpeg.avformat_open_input(&format, path, null, null), $"Opening '{path}'");
        _format = format;

        FfmpegLoader.Check(ffmpeg.avformat_find_stream_info(_format, null), "Reading stream info");

        AVCodec* codec = null;
        _streamIndex = FfmpegLoader.Check(
            ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0),
            $"Finding an audio stream in '{path}'");

        _codec = ffmpeg.avcodec_alloc_context3(codec);
        if (_codec is null) throw new FfmpegDecodeException("Could not allocate an audio decoder context.");

        FfmpegLoader.Check(
            ffmpeg.avcodec_parameters_to_context(_codec, _format->streams[_streamIndex]->codecpar),
            "Configuring the audio decoder");

        FfmpegLoader.Check(ffmpeg.avcodec_open2(_codec, codec, null), "Opening the audio decoder");

        var stream = _format->streams[_streamIndex];
        if (stream->duration > 0)
            DurationSeconds = stream->duration * ffmpeg.av_q2d(stream->time_base);
        else if (_format->duration > 0)
            DurationSeconds = _format->duration / (double)ffmpeg.AV_TIME_BASE;

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        // AV_SAMPLE_FMT_FLT rather than FLTP: interleaved lands in a single plane, so a
        // converted block copies straight into a float[] with no per-channel pass.
        AVChannelLayout outputLayout;
        ffmpeg.av_channel_layout_default(&outputLayout, Channels);

        SwrContext* resampler = null;

        try
        {
            FfmpegLoader.Check(
                ffmpeg.swr_alloc_set_opts2(
                    &resampler,
                    &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate,
                    &_codec->ch_layout, _codec->sample_fmt, _codec->sample_rate,
                    0, null),
                "Creating the resampler");

            _resampler = resampler;
            FfmpegLoader.Check(ffmpeg.swr_init(_resampler), "Initialising the resampler");
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&outputLayout);
        }
    }

    /// <summary>
    /// Positions the decoder so the next <see cref="Read"/> returns audio from
    /// <paramref name="seconds"/>.
    /// </summary>
    /// <remarks>
    /// Seeks to the preceding packet and then discards decoded samples up to the exact
    /// position, because audio packets hold hundreds of samples each and landing on a packet
    /// boundary would be up to ~20 ms out — enough to matter to a feature whose whole
    /// purpose is alignment.
    /// </remarks>
    public void SeekTo(double seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        seconds = Math.Max(0, seconds);

        var stream = _format->streams[_streamIndex];
        var target = (long)(seconds / ffmpeg.av_q2d(stream->time_base));

        FfmpegLoader.Check(
            ffmpeg.av_seek_frame(_format, _streamIndex, target, ffmpeg.AVSEEK_FLAG_BACKWARD),
            "Seeking audio");

        ffmpeg.avcodec_flush_buffers(_codec);
        DropPending();
        _drained = false;
        PositionSeconds = seconds;

        // The seek lands at or before the request; skip the difference so the caller's
        // sample zero really is the sample they asked for.
        while (true)
        {
            if (!Fill(out var blockStart)) return;

            var blockEnd = blockStart + (_pendingCount / (double)SampleRate);
            if (blockEnd <= seconds)
            {
                DropPending();
                continue;
            }

            var skip = (int)Math.Round((seconds - blockStart) * SampleRate);
            if (skip > 0)
            {
                skip = Math.Min(skip, _pendingCount);
                _pendingStart += skip * Channels;
                _pendingCount -= skip;
            }

            return;
        }
    }

    /// <summary>
    /// Reads up to <paramref name="frames"/> interleaved sample frames into
    /// <paramref name="destination"/>.
    /// </summary>
    /// <returns>
    /// How many sample frames were written. Zero means the stream ended; a short read does
    /// not, so callers loop until it returns zero.
    /// </returns>
    public int Read(float[] destination, int offset, int frames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);

        if (frames <= 0) return 0;

        var written = 0;

        while (written < frames)
        {
            if (_pendingCount == 0 && !Fill(out _)) break;
            if (_pendingCount == 0) break;

            var take = Math.Min(frames - written, _pendingCount);

            Array.Copy(
                _pending, _pendingStart,
                destination, offset + (written * Channels),
                take * Channels);

            _pendingStart += take * Channels;
            _pendingCount -= take;
            written += take;
        }

        PositionSeconds += written / (double)SampleRate;
        return written;
    }

    /// <summary>Decodes and converts one frame into <see cref="_pending"/>.</summary>
    /// <param name="blockStartSeconds">Where the converted block begins in the source.</param>
    private bool Fill(out double blockStartSeconds)
    {
        blockStartSeconds = PositionSeconds;

        while (true)
        {
            if (_pendingCount > 0) return true;
            if (_drained) return false;

            var received = ffmpeg.avcodec_receive_frame(_codec, _frame);

            if (received == 0)
            {
                var stream = _format->streams[_streamIndex];
                var pts = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                    ? _frame->best_effort_timestamp
                    : _frame->pts;

                if (pts != ffmpeg.AV_NOPTS_VALUE)
                    blockStartSeconds = pts * ffmpeg.av_q2d(stream->time_base);

                Convert();
                if (_pendingCount > 0) return true;
                continue;
            }

            if (received == ffmpeg.AVERROR_EOF)
            {
                _drained = true;
                return false;
            }

            if (received != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                FfmpegLoader.Check(received, "Receiving an audio frame");

            if (!TrySendNextPacket())
            {
                _drained = true;
                return false;
            }
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
                // Flush, so samples still inside the decoder come out before EOF is reported.
                ffmpeg.avcodec_send_packet(_codec, null);
                return true;
            }

            if (read < 0) return false;
            if (_packet->stream_index != _streamIndex) continue;

            var sent = ffmpeg.avcodec_send_packet(_codec, _packet);
            if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                FfmpegLoader.Check(sent, "Sending an audio packet");

            return true;
        }
    }

    private void Convert()
    {
        // swr holds samples back when rate-converting, so the output can exceed the input.
        var capacity = (int)ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(_resampler, _codec->sample_rate) + _frame->nb_samples,
            SampleRate,
            _codec->sample_rate,
            AVRounding.AV_ROUND_UP);

        if (capacity <= 0) return;

        var needed = capacity * Channels;
        if (_converted.Length < needed) _converted = new float[needed];

        int produced;

        fixed (float* target = _converted)
        {
            var planes = stackalloc byte*[1];
            planes[0] = (byte*)target;

            produced = ffmpeg.swr_convert(
                _resampler, planes, capacity, _frame->extended_data, _frame->nb_samples);
        }

        FfmpegLoader.Check(produced, "Resampling audio");
        if (produced == 0) return;

        var samples = produced * Channels;
        if (_pending.Length < samples) _pending = new float[samples];

        Array.Copy(_converted, 0, _pending, 0, samples);
        _pendingStart = 0;
        _pendingCount = produced;
    }

    private void DropPending()
    {
        _pendingStart = 0;
        _pendingCount = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_resampler is not null) { var r = _resampler; ffmpeg.swr_free(&r); _resampler = null; }
        if (_packet is not null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_frame is not null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_codec is not null) { var c = _codec; ffmpeg.avcodec_free_context(&c); _codec = null; }
        if (_format is not null) { var f = _format; ffmpeg.avformat_close_input(&f); _format = null; }
    }
}
