using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Media.Decode;

namespace BertCut.Media.Audio;

/// <summary>
/// Reads the edited timeline's audio as one continuous stream of interleaved samples.
/// </summary>
/// <remarks>
/// <para>
/// <b>Base track only</b>, which is not an omission: the export mixes the base track and
/// nothing else, and a preview that played something the file will not contain would break
/// the property the whole editor is built around. Two angles of one event carry near-identical
/// audio, so summing an overlay in would comb rather than enrich.
/// </para>
/// <para>
/// Each base segment contributes its own natural source duration — the same
/// <c>SecondsOf(start)</c> to <c>SecondsOf(end)</c> range <c>ExportPlanner</c> hands to
/// <c>atrim</c> — rather than a duration computed from the frame count and a nominal rate.
/// Under variable frame rate those differ, and matching the export is what matters.
/// </para>
/// <para>
/// Not thread-safe, and it owns decoders. The playback thread constructs one and keeps it;
/// nothing else touches that instance.
/// </para>
/// </remarks>
public sealed class TimelineAudioReader : IDisposable
{
    private readonly Project _project;
    private readonly Func<int, SourceIndex> _indexOf;
    private readonly Dictionary<int, AudioDecoder?> _decoders = [];
    private readonly int _sampleRate;

    private int _segment;
    private long _samplesIntoSegment;
    private bool _disposed;

    public TimelineAudioReader(Project project, Func<int, SourceIndex> indexOf)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(indexOf);

        _project = project;
        _indexOf = indexOf;
        _sampleRate = project.Output.SampleRate;
    }

    /// <summary>Output rate, per channel.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Stereo, matching the export's <c>-ac 2</c>.</summary>
    public int Channels => 2;

    /// <summary>True when every base segment has run out.</summary>
    public bool AtEnd => _segment >= _project.Base.Length;

    /// <summary>Where the next sample sits on the timeline, in output frames.</summary>
    public long PositionFrames
    {
        get
        {
            if (_project.Base.IsEmpty) return 0;
            if (AtEnd) return _project.DurationFrames;

            var segment = _project.Base[_segment];
            var seconds = _samplesIntoSegment / (double)_sampleRate;

            return segment.TimelineStart + (long)(seconds * _project.Output.FrameRate.Approx);
        }
    }

    /// <summary>Positions the stream at a timeline frame.</summary>
    public void SeekToFrame(long timelineFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_project.Base.IsEmpty)
        {
            _segment = 0;
            _samplesIntoSegment = 0;
            return;
        }

        timelineFrame = Math.Clamp(timelineFrame, 0, Math.Max(0, _project.DurationFrames));

        var index = 0;
        while (index < _project.Base.Length - 1
               && timelineFrame >= _project.Base[index].TimelineStart + _project.Base[index].LengthFrames)
        {
            index++;
        }

        _segment = index;

        var into = timelineFrame - _project.Base[index].TimelineStart;
        var seconds = into / _project.Output.FrameRate.Approx;
        _samplesIntoSegment = (long)(seconds * _sampleRate);

        PositionDecoder();
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with up to <paramref name="frames"/> interleaved
    /// stereo sample frames.
    /// </summary>
    /// <returns>
    /// How many sample frames were written. Zero means the timeline ended. Segments whose
    /// source has no audio produce silence of the right length rather than being skipped,
    /// so a silent clip in the middle of a project does not drag everything after it early.
    /// </returns>
    public int Read(float[] destination, int offset, int frames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);

        if (frames <= 0) return 0;

        var written = 0;

        while (written < frames && !AtEnd)
        {
            var remaining = SegmentSamples(_segment) - _samplesIntoSegment;

            if (remaining <= 0)
            {
                AdvanceSegment();
                continue;
            }

            var want = (int)Math.Min(frames - written, remaining);
            var target = offset + (written * Channels);

            var produced = ReadFromSegment(destination, target, want);

            if (produced < want)
            {
                // The source ran out early — pad to the segment's nominal length so the next
                // segment still starts where the timeline says it does.
                Array.Clear(destination, target + (produced * Channels), (want - produced) * Channels);
            }

            _samplesIntoSegment += want;
            written += want;
        }

        return written;
    }

    private int ReadFromSegment(float[] destination, int offset, int frames)
    {
        var decoder = DecoderFor(_project.Base[_segment].SourceId);

        if (decoder is null)
        {
            Array.Clear(destination, offset, frames * Channels);
            return frames;
        }

        return decoder.Read(destination, offset, frames);
    }

    private void AdvanceSegment()
    {
        _segment++;
        _samplesIntoSegment = 0;

        if (!AtEnd) PositionDecoder();
    }

    /// <summary>Seeks the current segment's decoder to where the timeline says it should be.</summary>
    private void PositionDecoder()
    {
        if (AtEnd) return;

        var segment = _project.Base[_segment];
        var decoder = DecoderFor(segment.SourceId);
        if (decoder is null) return;

        var index = _indexOf(segment.SourceId);
        var start = index.SecondsOf(Math.Clamp(segment.SourceStartFrame, 0, index.FrameCount - 1));

        decoder.SeekTo(start + (_samplesIntoSegment / (double)_sampleRate));
    }

    /// <summary>How many sample frames a base segment contributes.</summary>
    /// <remarks>
    /// Taken from the segment's source time range rather than from its frame count, which is
    /// what keeps a variable-frame-rate source's preview and export agreeing.
    /// </remarks>
    private long SegmentSamples(int index)
    {
        var segment = _project.Base[index];
        var sourceIndex = _indexOf(segment.SourceId);

        var startFrame = Math.Clamp(segment.SourceStartFrame, 0, sourceIndex.FrameCount - 1);
        var endFrame = Math.Clamp(
            segment.SourceStartFrame + segment.LengthFrames, 0, sourceIndex.FrameCount - 1);

        var seconds = sourceIndex.SecondsOf(endFrame) - sourceIndex.SecondsOf(startFrame);

        // A segment reaching the last frame has no following timestamp to measure against,
        // so fall back to the nominal length rather than reporting nothing.
        if (seconds <= 0) seconds = segment.LengthFrames / _project.Output.FrameRate.Approx;

        return (long)Math.Round(seconds * _sampleRate);
    }

    private AudioDecoder? DecoderFor(int sourceId)
    {
        if (_decoders.TryGetValue(sourceId, out var existing)) return existing;

        var source = _project.FindSource(sourceId);
        AudioDecoder? decoder = null;

        if (source is not null && source.HasAudio && File.Exists(source.Path))
        {
            try
            {
                decoder = new AudioDecoder(source.Path, _sampleRate, Channels);
            }
            catch (FfmpegDecodeException)
            {
                // A source that will not decode plays as silence; refusing to play anything
                // at all because one clip is broken would be worse.
                decoder = null;
            }
        }

        _decoders[sourceId] = decoder;
        return decoder;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var decoder in _decoders.Values) decoder?.Dispose();
        _decoders.Clear();
    }
}
