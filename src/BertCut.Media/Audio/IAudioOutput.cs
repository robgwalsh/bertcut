namespace BertCut.Media.Audio;

/// <summary>Something that can be pulled for interleaved float sample frames.</summary>
public interface IAudioSource
{
    /// <summary>
    /// Fills <paramref name="destination"/> with up to <paramref name="frames"/> interleaved
    /// sample frames, returning how many were written. Zero means the end.
    /// </summary>
    int Read(float[] destination, int offset, int frames);
}

/// <summary>
/// Somewhere rendered audio goes, and the clock that comes back from it.
/// </summary>
/// <remarks>
/// <para>
/// An interface for one reason above all: a harness run must not make noise on the user's
/// machine. Driving the real interface offscreen is only half of not disturbing someone —
/// sound out of their speakers while they work would be the same intrusion through a
/// different sense. <see cref="SilentAudioOutput"/> consumes the stream at real time and
/// discards it, so scripts still exercise playback, and the timing assertions still mean
/// something.
/// </para>
/// <para>
/// <see cref="RenderedFrames"/> is the whole point of the abstraction beyond that: it is the
/// position the playhead is derived from, so playback follows the sound rather than the two
/// racing each other.
/// </para>
/// </remarks>
public interface IAudioOutput : IDisposable
{
    /// <summary>Begins pulling <paramref name="source"/>.</summary>
    void Start(IAudioSource source, int sampleRate, int channels);

    /// <summary>Stops pulling and releases the device. Safe to call when not started.</summary>
    void Stop();

    /// <summary>
    /// Sample frames actually rendered since <see cref="Start"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately what has been <em>played</em>, not what has been read: the read position
    /// runs ahead by the whole buffer depth, and a playhead driven from it would sit visibly
    /// early against the sound.
    /// </remarks>
    long RenderedFrames { get; }

    /// <summary>True once the source has been drained and playback has finished.</summary>
    bool HasFinished { get; }
}
