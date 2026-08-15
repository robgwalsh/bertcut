using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BertCut.Media.Audio;

/// <summary>
/// Plays through the default output device in WASAPI shared mode.
/// </summary>
/// <remarks>
/// <para>
/// Shared mode rather than exclusive: an editor has no business taking the sound card away
/// from everything else the user is running, and shared mode's latency is far below what
/// matters for judging whether two angles line up.
/// </para>
/// <para>
/// <see cref="RenderedFrames"/> comes from the device's own position rather than from a count
/// of what has been handed to it. That number is the reason this class exists in the shape
/// it does — the playhead is derived from it, so the picture follows the sound instead of the
/// two drifting apart over a long take.
/// </para>
/// <para>
/// This is the only file in the solution that touches NAudio. Everything above it talks to
/// <see cref="IAudioOutput"/>, which is what lets the harness run the whole playback path in
/// silence.
/// </para>
/// </remarks>
public sealed class WasapiAudioOutput : IAudioOutput
{
    /// <summary>
    /// Requested buffer depth. Long enough to survive a decode hitch on a seek, short
    /// enough that stopping feels immediate.
    /// </summary>
    private const int LatencyMilliseconds = 120;

    private readonly Lock _gate = new();

    private WasapiPlayer? _device;
    private PullProvider? _provider;
    private bool _disposed;

    /// <summary>True when a default output device exists.</summary>
    /// <remarks>
    /// Checked before constructing a player so that a machine with no sound card falls back
    /// to <see cref="SilentAudioOutput"/> rather than failing to play the timeline at all.
    /// </remarks>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException)
            {
                return false;
            }
        }
    }

    public long RenderedFrames
    {
        get
        {
            WasapiPlayer? device;
            PullProvider? provider;

            lock (_gate)
            {
                device = _device;
                provider = _provider;
            }

            if (device is null || provider is null) return 0;

            try
            {
                // GetPosition is in bytes of the output format.
                return device.GetPosition() / provider.WaveFormat.BlockAlign;
            }
            catch (Exception e) when (e is COMException or InvalidOperationException)
            {
                // The device was removed mid-playback; fall back to what has been pulled so
                // the playhead keeps moving instead of freezing.
                return provider.FramesRead;
            }
        }
    }

    public bool HasFinished => _provider?.AtEnd ?? false;

    public void Start(IAudioSource source, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        var provider = new PullProvider(source, sampleRate, channels);

        var device = new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithLatency(LatencyMilliseconds)
            .WithEventSync()
            .Build();

        try
        {
            device.Init(provider);
            device.Play();
        }
        catch
        {
            device.Dispose();
            throw;
        }

        lock (_gate)
        {
            _provider = provider;
            _device = device;
        }
    }

    public void Stop()
    {
        WasapiPlayer? device;

        lock (_gate)
        {
            device = _device;
            _device = null;
            _provider = null;
        }

        if (device is null) return;

        try { device.Stop(); }
        catch (Exception e) when (e is COMException or InvalidOperationException) { /* device went away */ }

        device.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>Adapts an <see cref="IAudioSource"/> to what NAudio pulls from.</summary>
    /// <remarks>
    /// Once the source is exhausted this returns silence rather than a short read. A short
    /// read makes NAudio stop the device, and the position it reports stops with it — which
    /// would strand the playhead a buffer short of the end instead of letting it arrive.
    /// <see cref="AtEnd"/> is how the player learns the difference.
    /// </remarks>
    private sealed class PullProvider(IAudioSource source, int sampleRate, int channels)
        : IWaveProvider
    {
        private float[] _scratch = [];

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        /// <summary>Sample frames pulled so far — the fallback clock if the device's fails.</summary>
        public long FramesRead { get; private set; }

        public bool AtEnd { get; private set; }

        public int Read(Span<byte> buffer)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            var frames = samples.Length / channels;
            if (frames <= 0) return 0;

            // The source fills a float[], so one scratch buffer is kept and reused rather
            // than allocated per callback on the device thread.
            var needed = frames * channels;
            if (_scratch.Length < needed) _scratch = new float[needed];

            var read = AtEnd ? 0 : source.Read(_scratch, 0, frames);

            if (read < frames)
            {
                AtEnd = true;
                Array.Clear(_scratch, read * channels, (frames - read) * channels);
            }

            FramesRead += read;

            _scratch.AsSpan(0, needed).CopyTo(samples);
            return frames * channels * sizeof(float);
        }
    }
}
