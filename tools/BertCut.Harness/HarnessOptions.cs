namespace BertCut.Harness;

/// <summary>How a run was asked for.</summary>
internal sealed class HarnessOptions
{
    /// <summary>Commands to run, in order, already split into lines.</summary>
    public required IReadOnlyList<string> Commands { get; init; }

    /// <summary>Where captures and sample media land.</summary>
    public required string OutputDir { get; init; }

    /// <summary>The state root this run gets to itself.</summary>
    public required string StateDir { get; init; }

    /// <summary>False when the state directory is scratch and should be deleted afterwards.</summary>
    public bool KeepState { get; init; }

    /// <summary>Seconds before the watchdog gives up on the whole run.</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>How long a single open or import may take before it is called a hang.</summary>
    public int BusyTimeoutMs { get; init; } = 60_000;

    /// <summary>Carry on after a failed command rather than stopping at the first one.</summary>
    public bool KeepGoing { get; init; }

    /// <summary>
    /// Let preview audio reach the sound card.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately so. Parking the window offscreen is only half of not
    /// interrupting someone; sound out of their speakers while they work is the same
    /// intrusion through a different sense. With this off the whole playback path still runs
    /// — decoders, segment boundaries, the clock the playhead follows — into a sink that
    /// discards it at real time, so timing assertions still mean what they say.
    /// </remarks>
    public bool Audio { get; init; }

    public bool Verbose { get; init; }

    /// <summary>Exit codes, so a caller can tell a failed assertion from a broken environment.</summary>
    public static class Exit
    {
        public const int Ok = 0;
        public const int Failed = 1;
        public const int Environment = 2;
        public const int Timeout = 3;
        public const int Usage = 64;
    }

    private const string Usage = """
        BertCut UI harness — drives the real editor window offscreen, where it cannot take
        focus from you or appear over what you are doing.

          --script <path>     run the commands in a file
          -c "<commands>"     run commands given inline, separated by ';'
                              (with neither, commands are read from stdin)

          --out <dir>         where captures go        (default: %TEMP%\bertcut-harness\<time>)
          --state-dir <dir>   the app's state root      (default: a scratch dir, deleted after)
          --keep-state        keep the state directory, to test session restore across runs
          --timeout <sec>     watchdog, in seconds      (default: 120)
          --busy-timeout <ms> longest allowed open/import (default: 60000)
          --keep-going        do not stop at the first failure
          --audio             let preview audio reach the speakers (silent by default)
          --verbose
          --help

        Every command echoes 'OK <command>'. Captures print 'SHOT <path>' on their own line.
        """;

    /// <summary>Reads the command line, or explains itself and returns null.</summary>
    public static HarnessOptions? Parse(string[] args, TextWriter output, out int exitCode)
    {
        exitCode = Exit.Ok;

        string? scriptPath = null;
        string? inline = null;
        string? outputDir = null;
        string? stateDir = null;
        var keepState = false;
        var timeout = 120;
        var busyTimeout = 60_000;
        var keepGoing = false;
        var audio = false;
        var verbose = false;

        for (var i = 0; i < args.Length; i++)
        {
            string Next(string flag) =>
                i + 1 < args.Length
                    ? args[++i]
                    : throw new FormatException($"{flag} needs a value.");

            switch (args[i])
            {
                case "--script": scriptPath = Next("--script"); break;
                case "-c" or "--command": inline = Next("-c"); break;
                case "--out": outputDir = Next("--out"); break;
                case "--state-dir": stateDir = Next("--state-dir"); break;
                case "--keep-state": keepState = true; break;
                case "--timeout": timeout = int.Parse(Next("--timeout")); break;
                case "--busy-timeout": busyTimeout = int.Parse(Next("--busy-timeout")); break;
                case "--keep-going": keepGoing = true; break;
                case "--audio": audio = true; break;
                case "--verbose": verbose = true; break;

                case "--help" or "-h" or "/?":
                    output.WriteLine(Usage);
                    return null;

                default:
                    output.WriteLine($"Unrecognised argument '{args[i]}'.");
                    output.WriteLine();
                    output.WriteLine(Usage);
                    exitCode = Exit.Usage;
                    return null;
            }
        }

        IReadOnlyList<string> commands;

        if (scriptPath is not null)
        {
            if (!File.Exists(scriptPath))
            {
                output.WriteLine($"No script at '{scriptPath}'.");
                exitCode = Exit.Environment;
                return null;
            }

            commands = File.ReadAllLines(scriptPath);
        }
        else if (inline is not null)
        {
            commands = inline.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else
        {
            commands = (Console.In.ReadToEnd() ?? "").Split('\n');
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        return new HarnessOptions
        {
            Commands = commands,
            OutputDir = Path.GetFullPath(
                outputDir ?? Path.Combine(Path.GetTempPath(), "bertcut-harness", stamp)),
            StateDir = Path.GetFullPath(
                stateDir ?? Path.Combine(Path.GetTempPath(), "bertcut-harness", stamp, "state")),
            KeepState = keepState || stateDir is not null,
            TimeoutSeconds = timeout,
            BusyTimeoutMs = busyTimeout,
            KeepGoing = keepGoing,
            Audio = audio,
            Verbose = verbose,
        };
    }
}
