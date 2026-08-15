using System.Diagnostics;

namespace BertCut.Harness;

/// <summary>
/// Keeps the foreground with whoever had it when the run started.
/// </summary>
/// <remarks>
/// <para>
/// Showing the window does not activate it and WPF never raises <c>Activated</c>, yet the
/// window is measurably made foreground once during startup — for about an eighth of a second
/// — and neither <c>WS_EX_NOACTIVATE</c>, disabling the window, nor refusing WPF focus
/// prevents it. Since the whole premise here is that the user can keep typing, the answer is
/// to watch for it and give the foreground straight back.
/// </para>
/// <para>
/// A poll rather than a hook because the activation arrives through no event this process
/// sees. Ten milliseconds is far below the interval at which a person notices a focus change,
/// and the cost is one Win32 call per tick.
/// </para>
/// </remarks>
internal sealed class ForegroundGuard : IDisposable
{
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stopping = new();
    private int _corrections;

    private ForegroundGuard(nint owner, int selfProcessId, TextWriter log)
    {
        _thread = new Thread(() =>
        {
            while (!_stopping.IsCancellationRequested)
            {
                if (Native.ForegroundProcessId() == selfProcessId)
                {
                    Interlocked.Increment(ref _corrections);
                    Native.RestoreForeground(owner);
                }

                _stopping.Token.WaitHandle.WaitOne(10);
            }

            if (_corrections > 0)
                log.WriteLine(
                    $"# the window reached the foreground {_corrections} time(s); each was handed back within ~10ms.");
        })
        {
            IsBackground = true,
            Name = "foreground-guard",
        };
    }

    /// <summary>Starts guarding, unless nothing had the foreground to begin with.</summary>
    public static ForegroundGuard Start(nint owner, TextWriter log)
    {
        var guard = new ForegroundGuard(owner, Environment.ProcessId, log);
        guard._thread.Start();

        return guard;
    }

    /// <summary>How many times the window had to be pushed back out of the foreground.</summary>
    public int Corrections => Volatile.Read(ref _corrections);

    public void Dispose()
    {
        _stopping.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stopping.Dispose();

        Debug.Assert(!_thread.IsAlive, "the foreground guard should stop when asked.");
    }
}
