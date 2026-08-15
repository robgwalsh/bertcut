using System.Globalization;
using System.Windows;
using BertCut.Core.Input;
using BertCut.Media;

namespace BertCut.Harness;

/// <summary>Raised when a command asserts something that is not so.</summary>
internal sealed class AssertionException(string message) : Exception(message);

/// <summary>
/// Runs a script against a hosted editor window.
/// </summary>
/// <remarks>
/// <para>
/// The command set is deliberately small and its vocabulary is the app's own: intents,
/// gestures, frame numbers and the names of elements as the XAML spells them. Anything that
/// would put a modal dialog on the user's screen is refused rather than driven, because a
/// file picker appearing over their work is precisely the failure this harness exists to
/// prevent.
/// </para>
/// </remarks>
internal sealed class ScriptRunner(UiSession session, HarnessOptions options, TextWriter output)
{
    private int _shots;

    /// <summary>Intents that open a modal Win32 dialog, and what to use instead.</summary>
    /// <remarks>
    /// These reach the screen no matter where the window is parked — a common file dialog is
    /// owned by the desktop, not by an offscreen window — so they are the one part of the app
    /// this harness will not exercise.
    /// </remarks>
    private static readonly Dictionary<EditorIntent, string> DialogIntents = new()
    {
        [EditorIntent.OpenFile] = "open <path>",
        [EditorIntent.ImportSource] = "import <path>",
        [EditorIntent.AppendSource] = "append <path>",
        [EditorIntent.Export] = "export is not driven by the harness; EndToEndExportTests covers it headlessly",
    };

    /// <summary>Runs every command. Returns the process exit code.</summary>
    public int Run()
    {
        var failed = false;

        foreach (var raw in options.Commands)
        {
            var line = Strip(raw);
            if (line.Length == 0) continue;

            try
            {
                Execute(line);
                output.WriteLine($"OK {line}");
            }
            catch (Exception e) when (e is AssertionException or FormatException or InvalidOperationException
                                       or TimeoutException or IOException or ArgumentException)
            {
                output.WriteLine($"FAIL {line} — {e.Message}");
                failed = true;

                if (!options.KeepGoing) break;
            }
        }

        return failed ? HarnessOptions.Exit.Failed : HarnessOptions.Exit.Ok;
    }

    /// <summary>Drops comments and surrounding space; '#' only starts one at the front of a line.</summary>
    private static string Strip(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith('#') ? "" : trimmed;
    }

    private void Execute(string line)
    {
        var (verb, rest) = Split(line);

        switch (verb.ToLowerInvariant())
        {
            case "echo": output.WriteLine(rest); break;

            case "sample": Sample(rest); break;
            case "open": Open(rest); break;
            case "import": Load(rest, path => session.Model.ImportAsync(path)); break;
            case "append": Load(rest, path => session.Model.AppendAsync(path)); break;

            case "key": Key(rest); break;
            case "intent": Intent(rest); break;

            case "goto": Goto(rest); break;
            case "play": session.Dispatch(EditorIntent.PlayPause); break;
            case "stop": session.Dispatch(EditorIntent.Stop); break;
            case "tick": Tick(rest); break;
            case "sleep": Thread.Sleep(Number(rest, "sleep")); break;
            case "reset": session.Model.ResetAll(); session.Settle(); break;
            case "settle": session.Settle(rest.Length == 0 ? 0 : Number(rest, "settle")); break;

            case "shot": Shot(rest); break;
            case "dump-preview": DumpPreview(rest); break;
            case "state": output.WriteLine("STATE " + State()); break;

            case "assert-status": AssertStatus(rest); break;
            case "assert-timecode": AssertTimecode(rest); break;
            case "assert-frame": AssertFrame(rest); break;
            case "assert-frame-between": AssertFrameBetween(rest); break;
            case "assert-visible": AssertVisibility(rest, Visibility.Visible); break;
            case "assert-hidden": AssertVisibility(rest, Visibility.Visible, expected: false); break;
            case "assert-has-media": AssertHasMedia(); break;

            default:
                throw new FormatException($"'{verb}' is not a command. Run with --help for the list.");
        }
    }

    // ---- loading ----------------------------------------------------------------------

    private void Sample(string rest)
    {
        var (path, tail) = Split(rest);
        if (path.Length == 0) throw new FormatException("sample needs a file name.");

        var seconds = tail.Length == 0 ? 6 : Number(tail, "sample");

        SampleMedia.Write(session.Runtime, Resolve(path), seconds);
    }

    private void Open(string rest)
    {
        var path = Resolve(Require(rest, "open"));
        if (!File.Exists(path)) throw new FileNotFoundException($"No file at '{path}'.", path);

        session.Dispatcher.Invoke(() => session.Window.OpenPath(path));
        session.Settle();
    }

    private void Load(string rest, Func<string, Task> load)
    {
        var path = Resolve(Require(rest, "import"));
        if (!File.Exists(path)) throw new FileNotFoundException($"No file at '{path}'.", path);

        session.Dispatcher.Invoke(() => _ = load(path));
        session.Settle();
    }

    // ---- driving ----------------------------------------------------------------------

    private void Key(string rest)
    {
        var (key, modifiers) = Gesture.Parse(Require(rest, "key"));

        var intent = session.Dispatcher.Invoke(() =>
        {
            var resolved = session.Window.Bindings.Resolve(key, modifiers, session.Model.Mode);

            if (DialogIntents.TryGetValue(resolved, out var instead))
                throw new InvalidOperationException(
                    $"{rest} is bound to {resolved}, which opens a modal dialog on the user's screen. Use: {instead}.");

            return session.PressKey(key, modifiers);
        });

        if (intent == EditorIntent.None)
            throw new AssertionException($"'{rest}' is not bound to anything in {session.Model.Mode} mode.");

        session.Settle();
    }

    private void Intent(string rest)
    {
        var name = Require(rest, "intent");

        if (!Enum.TryParse<EditorIntent>(name, ignoreCase: true, out var intent) || intent == EditorIntent.None)
            throw new FormatException($"'{name}' is not an editor intent.");

        if (DialogIntents.TryGetValue(intent, out var instead))
            throw new InvalidOperationException(
                $"{intent} opens a modal dialog on the user's screen. Use: {instead}.");

        session.Dispatcher.Invoke(() => session.Dispatch(intent));
        session.Settle();
    }

    private void Goto(string rest)
    {
        var frame = Number(rest, "goto");
        session.Dispatcher.Invoke(() => session.Model.ScrubTo(frame));
        session.Settle();
    }

    private void Tick(string rest)
    {
        var times = rest.Length == 0 ? 1 : Number(rest, "tick");

        session.Dispatcher.Invoke(() =>
        {
            for (var i = 0; i < times; i++) session.Model.Tick();
        });

        session.Settle();
    }

    // ---- capturing --------------------------------------------------------------------

    private void Shot(string rest)
    {
        var (name, elementName) = Split(rest);
        var path = Resolve(Named(name, ++_shots));

        var (width, height) = session.Dispatcher.Invoke(() =>
        {
            var element = elementName.Length == 0
                ? session.Window
                : Find(elementName);

            return Capture.Save(element, path);
        });

        if (!Capture.HasContent(path))
            throw new AssertionException(
                $"{path} is a single flat colour — the window rendered nothing. See V2 in the harness notes.");

        output.WriteLine($"SHOT {path}");
        if (options.Verbose) output.WriteLine($"# {width}x{height}");
    }

    private void DumpPreview(string rest)
    {
        var path = Resolve(Named(rest, ++_shots));

        var frame = session.Dispatcher.Invoke(() => session.PreviewFrame)
            ?? throw new AssertionException("There is no composited frame; open a video first.");

        Png.Save(frame, path);
        output.WriteLine($"SHOT {path}");
    }

    private static string Named(string name, int ordinal)
    {
        if (name.Length == 0) return $"{ordinal:00}-shot.png";

        return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png";
    }

    // ---- reading back -----------------------------------------------------------------

    /// <summary>
    /// Everything worth asserting about, on one line.
    /// </summary>
    /// <remarks>
    /// JSON because a run is usually read by whatever asked for it, and one line because a
    /// script's output is read as a transcript where a pretty-printed object would bury the
    /// commands around it.
    /// </remarks>
    private string State() => session.Dispatcher.Invoke(() =>
    {
        var model = session.Model;

        var fields = new (string Name, string Value)[]
        {
            ("playhead", Text(model.Playhead)),
            ("duration", Text(model.DurationFrames)),
            ("markIn", model.MarkIn is { } markIn ? Text(markIn) : "null"),
            ("markOut", model.MarkOut is { } markOut ? Text(markOut) : "null"),
            ("mode", Quote(model.Mode.ToString())),
            ("hasMedia", model.HasMedia ? "true" : "false"),
            ("crops", Text(model.Project.Crops.Length)),
            ("overlays", Text(model.Project.Overlays.Length)),
            ("canUndo", model.CanUndo ? "true" : "false"),
            ("status", Quote(model.Status)),
        };

        return "{" + string.Join(",", fields.Select(f => $"{Quote(f.Name)}:{f.Value}")) + "}";
    });

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private void AssertStatus(string rest)
    {
        var expected = Require(rest, "assert-status");
        var actual = session.Dispatcher.Invoke(() => session.Model.Status);

        if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new AssertionException($"expected the status to contain '{expected}', got '{actual}'.");
    }

    private void AssertTimecode(string rest)
    {
        var expected = Require(rest, "assert-timecode");
        var actual = session.Dispatcher.Invoke(() => session.Model.TimecodeText);

        if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new AssertionException($"expected the timecode to contain '{expected}', got '{actual}'.");
    }

    private void AssertFrame(string rest)
    {
        var expected = Number(rest, "assert-frame");
        var actual = session.Dispatcher.Invoke(() => session.Model.Playhead);

        if (actual != expected)
            throw new AssertionException($"expected frame {expected}, got {actual}.");
    }

    /// <summary>
    /// Asserts the playhead is somewhere in a range.
    /// </summary>
    /// <remarks>
    /// Playback advances off a real stopwatch, so the only honest assertion about it is a
    /// bounded one. Anything that needs an exact frame should get there with <c>goto</c>.
    /// </remarks>
    private void AssertFrameBetween(string rest)
    {
        var (lowText, highText) = Split(rest);
        var low = Number(lowText, "assert-frame-between");
        var high = Number(highText, "assert-frame-between");

        var actual = session.Dispatcher.Invoke(() => session.Model.Playhead);

        if (actual < low || actual > high)
            throw new AssertionException($"expected a frame in [{low}, {high}], got {actual}.");
    }

    private void AssertVisibility(string rest, Visibility visibility, bool expected = true)
    {
        var name = Require(rest, "assert-visible");

        var actual = session.Dispatcher.Invoke(() => Find(name).Visibility == visibility);

        if (actual != expected)
            throw new AssertionException(
                $"expected {name} to be {(expected ? "visible" : "not visible")}, it is not.");
    }

    private void AssertHasMedia()
    {
        if (!session.Dispatcher.Invoke(() => session.Model.HasMedia))
            throw new AssertionException("no video is open.");
    }

    private FrameworkElement Find(string name) =>
        session.Window.FindName(name) as FrameworkElement
        ?? throw new FormatException($"there is no element named '{name}' in the window.");

    // ---- odds and ends ----------------------------------------------------------------

    /// <summary>Splits a line into its first word and everything after it.</summary>
    private static (string Head, string Tail) Split(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..].Trim());
    }

    private static string Require(string rest, string verb) =>
        rest.Length > 0 ? rest : throw new FormatException($"{verb} needs an argument.");

    private static int Number(string text, string verb) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{verb} needs a number, got '{text}'.");

    /// <summary>Resolves a bare name against the run's output directory.</summary>
    private string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(options.OutputDir, path);
}
