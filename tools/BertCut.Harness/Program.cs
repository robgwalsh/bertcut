using System.Windows.Threading;
using BertCut.Harness;

// The harness runs the editor on its own STA thread with its own dispatcher, rather than
// through Application.Run, so that the script can drive the window from the outside and the
// process still ends when the script does.

var output = Console.Out;

var options = HarnessOptions.Parse(args, output, out var parseExit);
if (options is null) return parseExit;

// Nothing here has a window the user could find and close, so a run that wedges would sit in
// the background forever. The watchdog is what makes an invisible process safe to start.
using var watchdog = new Timer(
    _ =>
    {
        output.WriteLine($"FAIL watchdog — nothing finished within {options.TimeoutSeconds}s.");
        output.Flush();
        Environment.Exit(HarnessOptions.Exit.Timeout);
    },
    null,
    TimeSpan.FromSeconds(options.TimeoutSeconds),
    Timeout.InfiniteTimeSpan);

Directory.CreateDirectory(options.OutputDir);
output.WriteLine($"# out:   {options.OutputDir}");
output.WriteLine($"# state: {options.StateDir}");

var exitCode = HarnessOptions.Exit.Environment;

var thread = new Thread(() =>
{
    UiSession? session = null;

    try
    {
        session = UiSession.Start(options, output);
        exitCode = new ScriptRunner(session, options, output).Run();
    }
    catch (Exception e)
    {
        output.WriteLine($"FAIL harness — {e.Message}");
        exitCode = HarnessOptions.Exit.Environment;
    }
    finally
    {
        session?.Dispose();
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }
})
{
    IsBackground = true,
};

thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

if (!options.KeepState)
{
    try
    {
        if (Directory.Exists(options.StateDir)) Directory.Delete(options.StateDir, recursive: true);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        output.WriteLine($"# could not remove the scratch state directory: {e.Message}");
    }
}

output.Flush();

// WPF leaves foreground threads of its own behind, and a harness that does not come back is
// worse than one that fails, so the exit is explicit rather than a return from Main.
Environment.Exit(exitCode);
return exitCode;
