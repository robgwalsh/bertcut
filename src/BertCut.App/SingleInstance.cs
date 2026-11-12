using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using BertCut.Core.Session;

namespace BertCut.App;

/// <summary>
/// One editor per state root: the first copy listens, a later one hands over and leaves.
/// </summary>
/// <remarks>
/// <para>
/// Two copies on one state directory write over each other's sessions, and "open with BertCut"
/// from a file browser used to start a new one every time. Now the second launch sends its
/// path to the first and exits, and the window already open comes forward with the file.
/// </para>
/// <para>
/// <strong>A hand-off that fails starts a second copy anyway.</strong> If the running copy
/// does not answer within <see cref="HandOffTimeout"/> it is hung or on its way out, and the
/// person who just opened a video is owed an editor more than the rule is owed its tidiness.
/// </para>
/// <para>
/// The pipe is open to this user only (<see cref="PipeOptions.CurrentUserOnly"/>), and what
/// arrives on it is still checked by <see cref="SingleInstanceNames.TryParse"/>: the same user
/// runs plenty of things that are not BertCut.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>How long a second launch waits for the first to take its request.</summary>
    private static readonly TimeSpan HandOffTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Room for the longest acceptable path in UTF-8, and no more.</summary>
    private const int MaxLineBytes = LaunchArgs.MaxPathLength * 4;

    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, string pipe)
    {
        _mutex = mutex;
        _pipe = pipe;
    }

    /// <summary>
    /// Becomes the running copy, if nothing else is.
    /// </summary>
    /// <returns>Null when another copy already owns this state root.</returns>
    public static SingleInstance? TryClaim()
    {
        var root = AppPaths.Root;
        var mutex = new Mutex(initiallyOwned: false, SingleInstanceNames.Mutex(root), out var created);

        if (created) return new SingleInstance(mutex, SingleInstanceNames.Pipe(root));

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Gives this launch's request to the copy already running.
    /// </summary>
    /// <returns>False when nothing took it, and this copy should start after all.</returns>
    public static bool HandOff(LaunchArgs args)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", SingleInstanceNames.Pipe(AppPaths.Root), PipeDirection.Out, PipeOptions.CurrentUserOnly);

            client.Connect(HandOffTimeout);

            // This process was started by a click, so it holds the right to choose the
            // foreground window and the running copy does not. Passing it on is what lets the
            // editor come forward instead of flashing in the taskbar.
            AllowSetForegroundWindow(AnyProcess);

            var bytes = Encoding.UTF8.GetBytes(SingleInstanceNames.Format(args));
            client.Write(bytes, 0, bytes.Length);
            client.Flush();

            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts taking requests from later launches.
    /// </summary>
    /// <param name="onRequest">
    /// Called on a pool thread with the path to open, or null for "just come forward".
    /// </param>
    public void Listen(Action<string?> onRequest) => _ = Task.Run(() => ListenAsync(onRequest));

    private async Task ListenAsync(Action<string?> onRequest)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipe,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(_stop.Token);

                if (SingleInstanceNames.TryParse(await ReadLineAsync(server, _stop.Token), out var path))
                    onRequest(path);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // One bad client must not end the listening. The pause is what keeps a pipe
                // that cannot be created from becoming a spin.
                try
                {
                    await Task.Delay(500, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// One line, or null when the sender wrote more than a path could ever be.
    /// </summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellation)
    {
        var buffer = new byte[MaxLineBytes + 1];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellation);
            if (read == 0) break;

            length += read;
            if (Array.IndexOf(buffer, (byte)'\n', 0, length) >= 0) break;
        }

        if (length > MaxLineBytes) return null;

        var text = Encoding.UTF8.GetString(buffer, 0, length);
        var end = text.IndexOf('\n');

        return end >= 0 ? text[..end] : text;
    }

    public void Dispose()
    {
        // Cancelled and not disposed: the listener may be between two reads of the token, and
        // the process is on its way out either way.
        _stop.Cancel();
        _mutex.Dispose();
    }

    private const int AnyProcess = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
