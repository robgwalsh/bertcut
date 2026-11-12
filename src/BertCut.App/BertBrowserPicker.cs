using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using BertCut.Core.Picking;
using BertCut.Core.Session;
using Microsoft.Win32;

namespace BertCut.App;

/// <summary>
/// Asks for a file in BertBrowser, when it is installed; in the Windows dialog otherwise.
/// </summary>
/// <remarks>
/// <para>
/// The conversation: this makes a pipe, starts BertBrowser with <c>--pick</c> and the pipe's
/// name, and BertBrowser — the copy already running, if there is one — opens a tab with the
/// question across the top and connects. One line comes back, the path chosen, or the pipe
/// closes with nothing on it, which is backing out. Nothing but that path crosses, and
/// BertBrowser writes nothing: a save is answered with where, and the writing happens here.
/// </para>
/// <para>
/// <strong>Every way it can go wrong ends at the Windows dialog or at "nothing chosen", never
/// at a dead window.</strong> BertBrowser not installed, too old, failing to start, or not
/// connecting within <see cref="ConnectTimeout"/> all fall back to the dialog. Once it has
/// connected there is no timeout — choosing a file takes as long as it takes — so the window
/// stays live and offers a Cancel of its own, which hangs up; BertBrowser notices and takes
/// its banner down.
/// </para>
/// <para>
/// Looked for on every question rather than once at startup, so installing or removing
/// BertBrowser takes effect without restarting the editor. It is one file probe.
/// </para>
/// </remarks>
internal sealed class BertBrowserPicker(MainWindow owner, IFilePicker fallback) : IFilePicker
{
    /// <summary>
    /// How long BertBrowser has to take the request. A copy already running answers in
    /// milliseconds; this is for one that has to start from cold.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Room for the longest acceptable path in UTF-8, and no more.</summary>
    private const int MaxLineBytes = LaunchArgs.MaxPathLength * 4;

    public async Task<string?> PickAsync(PickRequest request, CancellationToken cancellation = default)
    {
        if (Locate() is not { } exe) return await fallback.PickAsync(request, cancellation);

        var pipeName = BertBrowserArgs.PipeName(Environment.ProcessId, Guid.NewGuid());

        NamedPipeServerStream server;

        try
        {
            // Both directions, though only BertBrowser writes: reading access is what lets its
            // end notice this one hanging up.
            server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return await fallback.PickAsync(request, cancellation);
        }

        await using (server)
        {
            if (!Start(exe, BertBrowserArgs.Build(request, pipeName)))
                return await fallback.PickAsync(request, cancellation);

            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            owner.ShowPicking(waiting.Cancel);

            try
            {
                using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(waiting.Token))
                {
                    connecting.CancelAfter(ConnectTimeout);

                    try
                    {
                        await server.WaitForConnectionAsync(connecting.Token);
                    }
                    catch (OperationCanceledException) when (!waiting.IsCancellationRequested)
                    {
                        // It never took the request. The question still needs an answer.
                        owner.HidePicking();
                        return await fallback.PickAsync(request, cancellation);
                    }
                }

                var answer = await ReadLineAsync(server, waiting.Token);

                owner.HidePicking();
                owner.Activate();

                return Accept(request, answer);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                // Cancelled here, or BertBrowser went away mid-question. Either way: nothing chosen.
                return null;
            }
            finally
            {
                owner.HidePicking();
            }
        }
    }

    /// <summary>
    /// Decides whether what came back is an answer.
    /// </summary>
    /// <remarks>
    /// It arrived from another process, so it is checked like anything else that did: a plain
    /// full path or nothing. An open must name a file that is there. A save over a file that
    /// is there is asked about here, because this is the app that would do the overwriting —
    /// and "No" is "nothing chosen", which leaves the user one press from asking again.
    /// </remarks>
    private string? Accept(PickRequest request, string? answer)
    {
        if (answer is null || !LaunchArgs.IsAcceptablePath(answer)) return null;

        if (request.Kind == PickKind.Open) return File.Exists(answer) ? answer : null;

        if (Directory.Exists(answer)) return null;
        if (!File.Exists(answer)) return answer;

        var replace = MessageBox.Show(
            owner,
            $"{Path.GetFileName(answer)} already exists.{Environment.NewLine}Do you want to replace it?",
            request.Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return replace == MessageBoxResult.Yes ? answer : null;
    }

    private static bool Start(string exe, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };

        foreach (var arg in args) start.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>One line, or null for a pipe that closed without one (or sent more than a path could be).</summary>
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

        var end = Array.IndexOf(buffer, (byte)'\n', 0, length);

        // No newline means the line was never finished: the other end hung up, or overran.
        if (end < 0) return null;

        return Encoding.UTF8.GetString(buffer, 0, end).TrimEnd('\r');
    }

    private static string? Locate()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(BertBrowserLocator.UninstallKey);
            using var said = Registry.CurrentUser.OpenSubKey(BertBrowserLocator.PickerKey);

            return BertBrowserLocator.Locate(
                Environment.GetEnvironmentVariable(BertBrowserLocator.OverrideVariable),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                name => key?.GetValue(name) as string,
                File.Exists,
                said?.GetValue(BertBrowserLocator.PickerValue) as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
