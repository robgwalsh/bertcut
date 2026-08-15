using System.Diagnostics;

namespace BertCut.Media.Audio;

/// <summary>
/// Consumes audio at real time and discards it, reporting a clock as a device would.
/// </summary>
/// <remarks>
/// <para>
/// What the harness uses, so a scripted run exercises the whole playback path — the reader,
/// the decoders, the segment boundaries, the clock the playhead is derived from — without a
/// sound reaching the user. It is also the fallback in the app itself when no output device
/// can be opened, which keeps a machine with no sound card from losing the ability to play
/// the timeline at all.
/// </para>
/// <para>
/// The clock is a stopwatch rather than a count of frames consumed, because a fast reader
/// would otherwise drain the timeline as quickly as it could decode and playback would run
/// at whatever speed the disk allowed.
/// </para>
/// </remarks>
public sealed class SilentAudioOutput : IAudioOutput
{
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = new();

    private Thread? _thread;
    private CancellationTokenSource? _cancellation;
    private IAudioSource? _source;
    private int _sampleRate;
    private int _channels;
    private volatile bool _finished;
    private bool _disposed;

    public long RenderedFrames =>
        _sampleRate <= 0 ? 0 : (long)(_clock.Elapsed.TotalSeconds * _sampleRate);

    public bool HasFinished => _finished;

    public void Start(IAudioSource source, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        lock (_gate)
        {
            _source = source;
            _sampleRate = sampleRate;
            _channels = channels;
            _finished = false;

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            _clock.Restart();

            _thread = new Thread(() => Pump(token))
            {
                IsBackground = true,
                Name = "BertCut silent audio",
            };

            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? cancellation;

        lock (_gate)
        {
            thread = _thread;
            cancellation = _cancellation;
            _thread = null;
            _cancellation = null;
        }

        if (cancellation is null) return;

        cancellation.Cancel();
        thread?.Join(TimeSpan.FromSeconds(2));
        cancellation.Dispose();

        _clock.Stop();
    }

    /// <summary>Reads a block at a time, no faster than the clock says it has been played.</summary>
    private void Pump(CancellationToken token)
    {
        const int BlockMilliseconds = 20;

        var source = _source;
        if (source is null) return;

        var blockFrames = Math.Max(1, _sampleRate / (1000 / BlockMilliseconds));
        var buffer = new float[blockFrames * _channels];

        long consumed = 0;

        while (!token.IsCancellationRequested)
        {
            // Stay one block ahead of the clock and no more, so the source is pulled at the
            // rate a device would pull it.
            var due = (long)(_clock.Elapsed.TotalSeconds * _sampleRate) + blockFrames;

            if (consumed >= due)
            {
                if (token.WaitHandle.WaitOne(BlockMilliseconds / 2)) break;
                continue;
            }

            var read = source.Read(buffer, 0, blockFrames);
            if (read == 0)
            {
                _finished = true;
                break;
            }

            consumed += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
