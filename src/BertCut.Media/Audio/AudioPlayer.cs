using BertCut.Core.Media;
using BertCut.Core.Model;

namespace BertCut.Media.Audio;

/// <summary>
/// Plays the edited timeline's audio, and is the clock the playhead follows while it does.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="PreviewEngine"/>: same construction shape, same lifetime,
/// driven from the same transport. Where the preview engine is pulled once per displayed
/// frame by the UI thread, this pushes continuously from a device thread — so
/// <see cref="PositionFrames"/> is the one thing the UI thread reads from it, and it reads
/// a <c>long</c> that only the device's own position feeds.
/// </para>
/// <para>
/// <b>Only at 1x forward.</b> Shuttle and reverse leave this stopped and playback falls back
/// to the stopwatch it has always used. Pitch-preserving resampling for an 8x scrub would be
/// a lot of machinery for something nobody listens to, and the transport already says on
/// screen that you are off normal speed.
/// </para>
/// <para>
/// Muting zeroes the samples on their way out rather than stopping the device. Keeping the
/// same clock running across a mute is what stops the picture jumping when the key is
/// pressed mid-playback.
/// </para>
/// </remarks>
public sealed class AudioPlayer : IDisposable
{
    private readonly Func<IAudioOutput> _outputFactory;
    private readonly Lock _gate = new();

    private IAudioOutput? _output;
    private TimelineAudioReader? _reader;
    private double _frameRate = 30;
    private long _startFrame;
    private bool _disposed;

    /// <param name="outputFactory">
    /// Supplies the device. The app passes one that prefers WASAPI and falls back to silence;
    /// the harness passes one that is always silent.
    /// </param>
    public AudioPlayer(Func<IAudioOutput> outputFactory)
    {
        ArgumentNullException.ThrowIfNull(outputFactory);
        _outputFactory = outputFactory;
    }

    /// <summary>An output that uses the sound card when there is one, and silence otherwise.</summary>
    /// <remarks>
    /// A missing or broken output device degrades to silent playback rather than to no
    /// playback: being unable to hear the timeline should not stop you cutting it.
    /// </remarks>
    public static IAudioOutput DefaultOutput() =>
        WasapiAudioOutput.IsAvailable ? new WasapiAudioOutput() : new SilentAudioOutput();

    /// <summary>True while the device is running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>True once the timeline's audio has run out.</summary>
    public bool HasFinished => _output?.HasFinished ?? false;

    /// <summary>Silences the output without stopping the clock.</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// Where the sound currently leaving the device sits on the timeline, in output frames.
    /// </summary>
    public long PositionFrames
    {
        get
        {
            var output = _output;
            if (output is null || _reader is null) return _startFrame;

            var seconds = output.RenderedFrames / (double)_reader.SampleRate;
            return _startFrame + (long)(seconds * _frameRate);
        }
    }

    /// <summary>
    /// Starts playing <paramref name="project"/> from <paramref name="fromFrame"/>.
    /// </summary>
    /// <returns>
    /// False when there is nothing to play — an empty timeline, or no source with audio — in
    /// which case the caller keeps its own clock.
    /// </returns>
    public bool Start(Project project, Func<int, SourceIndex> indexOf, long fromFrame)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(indexOf);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        if (project.Base.IsEmpty) return false;
        if (!project.Sources.Any(s => s.HasAudio)) return false;

        lock (_gate)
        {
            var reader = new TimelineAudioReader(project, indexOf);

            try
            {
                reader.SeekToFrame(fromFrame);

                _reader = reader;
                _frameRate = project.Output.FrameRate.Approx;
                _startFrame = fromFrame;

                _output = _outputFactory();
                _output.Start(new Gate(this, reader), reader.SampleRate, reader.Channels);
            }
            catch
            {
                reader.Dispose();
                _reader = null;
                _output = null;
                throw;
            }

            IsRunning = true;
            return true;
        }
    }

    /// <summary>Stops the device and releases its decoders. Safe to call when not started.</summary>
    public void Stop()
    {
        IAudioOutput? output;
        TimelineAudioReader? reader;

        lock (_gate)
        {
            output = _output;
            reader = _reader;
            _output = null;
            _reader = null;
            IsRunning = false;
        }

        // The device thread reads through the reader, so it has to be stopped and joined
        // before the decoders under it are disposed.
        output?.Dispose();
        reader?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>Applies mute between the timeline and the device.</summary>
    private sealed class Gate(AudioPlayer player, TimelineAudioReader reader) : IAudioSource
    {
        public int Read(float[] destination, int offset, int frames)
        {
            var read = reader.Read(destination, offset, frames);

            if (player.Muted && read > 0)
                Array.Clear(destination, offset, read * reader.Channels);

            return read;
        }
    }
}
