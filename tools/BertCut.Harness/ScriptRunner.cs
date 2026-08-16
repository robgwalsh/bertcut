using System.Globalization;
using System.Windows;
using BertCut.App;
using BertCut.Core.Input;
using BertCut.Core.Model;
using BertCut.Core.Time;
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
        [EditorIntent.ChooseOverlayFile] = "overlay-source file <path>",
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
            case "sample-angles": SampleAngles(rest); break;
            case "open": Open(rest); break;
            case "import": Load(rest, path => session.Model.ImportAsync(path)); break;
            case "append": Load(rest, path => session.Model.AppendAsync(path)); break;

            case "key": Key(rest); break;
            case "intent": Intent(rest); break;
            case "overlay-source": OverlaySource(rest); break;

            case "select-overlay": SelectOverlay(rest); break;
            case "drag-overlay": DragOverlay(rest); break;
            case "trim-overlay": TrimOverlay(rest); break;
            case "select-segment": SelectSegment(rest); break;
            case "drag-segment": DragSegment(rest); break;
            case "scrub": Scrub(rest); break;

            case "goto": Goto(rest); break;
            case "play": session.Dispatch(EditorIntent.PlayPause); break;
            case "stop": session.Dispatch(EditorIntent.Stop); break;
            case "tick": Tick(rest); break;
            case "sleep": Thread.Sleep(Number(rest, "sleep")); break;
            case "reset": session.Model.ResetAll(); session.Settle(); break;
            case "close": Close(); break;
            case "settle": session.Settle(rest.Length == 0 ? 0 : Number(rest, "settle")); break;

            case "shot": Shot(rest); break;
            case "dump-preview": DumpPreview(rest); break;
            case "state": output.WriteLine("STATE " + State()); break;

            case "assert-status": AssertStatus(rest); break;
            case "assert-mode": AssertMode(rest); break;
            case "assert-timecode": AssertTimecode(rest); break;
            case "assert-frame": AssertFrame(rest); break;
            case "assert-frame-between": AssertFrameBetween(rest); break;
            case "assert-overlay-source-start": AssertOverlaySourceStart(rest); break;
            case "assert-overlay-selected": AssertOverlaySelected(rest); break;
            case "assert-no-overlay-selected": AssertNoOverlaySelected(); break;
            case "assert-overlay-start": AssertOverlayStart(rest); break;
            case "assert-overlay-end": AssertOverlayEnd(rest); break;
            case "assert-overlays": AssertOverlays(rest); break;
            case "assert-marks": AssertMarks(rest); break;
            case "assert-no-marks": AssertMarks(""); break;
            case "assert-segments": AssertSegments(rest); break;
            case "assert-segment-selected": AssertSegmentSelected(rest); break;
            case "assert-no-segment-selected": AssertNoSegmentSelected(); break;
            case "assert-muted": AssertMuted(rest, expected: true); break;
            case "assert-unmuted": AssertMuted(rest, expected: false); break;
            case "assert-visible": AssertVisibility(rest, Visibility.Visible); break;
            case "assert-hidden": AssertVisibility(rest, Visibility.Visible, expected: false); break;
            case "assert-has-media": AssertHasMedia(); break;
            case "assert-no-media": AssertNoMedia(); break;
            case "assert-unlocked": AssertUnlocked(rest); break;

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

    /// <summary>The two-angle fixture the audio sync is meant for.</summary>
    private void SampleAngles(string rest)
    {
        var (path, tail) = Split(rest);
        if (path.Length == 0) throw new FormatException("sample-angles needs a file name.");

        var seconds = tail.Length == 0 ? 6 : Number(tail, "sample-angles");

        SampleMedia.WriteTwoAngles(session.Runtime, Resolve(path), seconds);
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

    /// <summary>
    /// Takes one of the three rows on the overlay source card.
    /// </summary>
    /// <remarks>
    /// <c>range</c> and <c>segment</c> are what <c>key 1</c> and <c>key 2</c> do and are here
    /// only to read as what they are. <c>file</c> is the one that needs its own verb: the row
    /// opens a common file dialog, which belongs to the desktop and would appear on the user's
    /// screen wherever this window is parked — so the harness supplies the answer the picker
    /// would have given and drives everything after it, exactly as <c>import</c> does.
    /// </remarks>
    private void OverlaySource(string rest)
    {
        var (which, tail) = Split(Require(rest, "overlay-source"));

        switch (which.ToLowerInvariant())
        {
            case "range":
                session.Dispatch(EditorIntent.ChooseOverlayMarkedRange);
                break;

            case "segment":
                session.Dispatch(EditorIntent.ChooseOverlaySegment);
                break;

            case "cancel":
                session.Dispatch(EditorIntent.Cancel);
                break;

            case "file":
                var path = Resolve(Require(tail, "overlay-source file"));
                if (!File.Exists(path)) throw new FileNotFoundException($"No file at '{path}'.", path);

                session.Dispatcher.Invoke(() => _ = session.Model.ImportAndOverlayAsync(path));
                break;

            default:
                throw new FormatException(
                    $"'{which}' is not an overlay source. Use range, segment, file <path>, or cancel.");
        }

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

    // ---- the timeline's pointer -------------------------------------------------------

    /// <summary>
    /// Presses on the overlay band at a frame, as a click on the strip does.
    /// </summary>
    /// <remarks>
    /// Through <see cref="TimelineControl"/>'s own press-move-release methods — the ones its
    /// mouse handlers call — at a point the control works out for itself. Nothing here
    /// synthesises operating-system input, for the same reason keys do not: an offscreen
    /// window has no pointer over it, and the only real one belongs to the user. What this
    /// does exercise is everything above that line: the hit test, the grab offset, and the
    /// pixel-to-frame arithmetic that turns a position into an edit.
    /// </remarks>
    private void SelectOverlay(string rest)
    {
        var frame = Number(rest, "select-overlay");

        session.Dispatcher.Invoke(() =>
        {
            var timeline = session.Window.Timeline;
            timeline.PointerDown(timeline.OverlayBandPoint(frame));
            timeline.PointerUp();
        });

        session.Settle();
        RequireSelection("select-overlay", frame);
    }

    /// <summary>
    /// Drags the overlay under one frame until the grabbed point is over another.
    /// </summary>
    /// <remarks>
    /// In steps rather than one jump, because that is what a mouse produces and because the
    /// undo history is meant to coalesce them into a single step — a drag that moved in one
    /// call would pass whether it does or not.
    /// </remarks>
    private void DragOverlay(string rest)
    {
        var (fromText, toText) = Split(rest);
        DragBand(Number(fromText, "drag-overlay"), Number(toText, "drag-overlay"), "drag-overlay");
    }

    /// <summary>
    /// Drags one end of the selected overlay, which trims it rather than moving it.
    /// </summary>
    /// <remarks>
    /// The press lands on the clip's own edge, so which of the three gestures happens is
    /// still the control's decision from a pixel position — the same call a mouse makes. The
    /// selection is re-checked afterwards because two clips sitting end to end share an edge,
    /// and a press on it belongs to whichever band the hit test reaches first.
    /// </remarks>
    private void TrimOverlay(string rest)
    {
        var (edgeText, toText) = Split(rest);

        var start = edgeText.ToLowerInvariant() switch
        {
            "start" => true,
            "end" => false,
            _ => throw new FormatException($"trim-overlay needs 'start' or 'end', got '{edgeText}'."),
        };

        var to = Number(toText, "trim-overlay");

        var (index, clip) = session.Dispatcher.Invoke(() =>
            (session.Model.SelectedOverlay, session.Model.SelectedOverlayClip));

        if (clip is null) throw new AssertionException("trim-overlay: select an overlay first.");

        DragBand(start ? clip.Value.Range.Start : clip.Value.Range.End, to, "trim-overlay");

        if (session.Dispatcher.Invoke(() => session.Model.SelectedOverlay) != index)
            throw new AssertionException(
                $"trim-overlay: the press on that edge landed on overlay " +
                $"{session.Dispatcher.Invoke(() => session.Model.SelectedOverlay)}, not {index}.");
    }

    /// <summary>
    /// Clicks the ruler above the track, which seeks and nothing else.
    /// </summary>
    /// <remarks>
    /// The counterpart to <c>goto</c>, which reaches past the interface: this is the press a
    /// user makes when they want to move the playhead and not touch anything, and it is the
    /// only way to assert that clicking off the track lets go of a selection.
    /// </remarks>
    private void Scrub(string rest)
    {
        var frame = Number(rest, "scrub");

        session.Dispatcher.Invoke(() =>
        {
            var timeline = session.Window.Timeline;
            timeline.PointerDown(timeline.RulerPoint(frame));
            timeline.PointerUp();
        });

        session.Settle();
    }

    /// <summary>Clicks a base segment, on the part of the track clear of any overlay band.</summary>
    private void SelectSegment(string rest)
    {
        var frame = Number(rest, "select-segment");

        session.Dispatcher.Invoke(() =>
        {
            var timeline = session.Window.Timeline;
            timeline.PointerDown(timeline.SegmentPoint(frame));
            timeline.PointerUp();
        });

        session.Settle();
        RequireSegment("select-segment", frame);
    }

    /// <summary>
    /// Drags a base segment along the track, which rearranges the running order.
    /// </summary>
    /// <remarks>
    /// The first move is a nudge a few pixels out of the press. A real pointer crosses the
    /// drag threshold on its way anywhere; a scripted one has to as well, or the press stays
    /// the click that merely seeks and selects — which is exactly the distinction the
    /// threshold exists to draw, and therefore the one worth driving through rather than
    /// around.
    /// </remarks>
    private void DragSegment(string rest)
    {
        var (fromText, toText) = Split(rest);
        var from = Number(fromText, "drag-segment");
        var to = Number(toText, "drag-segment");

        session.Dispatcher.Invoke(() =>
        {
            var timeline = session.Window.Timeline;
            timeline.PointerDown(timeline.SegmentPoint(from));
        });

        session.Settle();
        RequireSegment("drag-segment", from);

        session.Dispatcher.Invoke(() =>
        {
            var timeline = session.Window.Timeline;
            var press = timeline.SegmentPoint(from);

            timeline.PointerMove(new Point(press.X + (to >= from ? Nudge : -Nudge), press.Y));

            for (var step = 1; step <= DragSteps; step++)
            {
                var frame = from + ((to - from) * step / DragSteps);
                timeline.PointerMove(timeline.SegmentPoint(frame));
            }

            timeline.PointerUp();
        });

        session.Settle();
    }

    /// <summary>Enough to clear the drag threshold, in the direction of travel.</summary>
    private const double Nudge = 6;

    private void RequireSegment(string verb, long frame)
    {
        if (session.Dispatcher.Invoke(() => session.Model.SelectedSegment) is null)
            throw new AssertionException($"{verb}: there is no base segment at frame {frame} to take hold of.");
    }

    /// <summary>Presses on the band at one frame, drags to another, and releases.</summary>
    private void DragBand(long from, long to, string verb)
    {
        session.Dispatcher.Invoke(() =>
        {
            var timeline = session.Window.Timeline;
            timeline.PointerDown(timeline.OverlayBandPoint(from));
        });

        session.Settle();

        // Before moving anything: a press that missed every band scrubbed instead, and the
        // drag that followed would silently be about nothing.
        RequireSelection(verb, from);

        session.Dispatcher.Invoke(() =>
        {
            var timeline = session.Window.Timeline;

            for (var step = 1; step <= DragSteps; step++)
            {
                var frame = from + ((to - from) * step / DragSteps);
                timeline.PointerMove(timeline.OverlayBandPoint(frame));
            }

            timeline.PointerUp();
        });

        session.Settle();
    }

    /// <summary>Pointer moves a drag is broken into.</summary>
    private const int DragSteps = 8;

    private void RequireSelection(string verb, long frame)
    {
        if (session.Dispatcher.Invoke(() => session.Model.SelectedOverlay) is null)
            throw new AssertionException(
                $"{verb}: there is no overlay band at frame {frame} to grab.");
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
            ("segments", Text(model.Project.Base.Length)),
            ("selectedSegment", model.SelectedSegment is { } segment ? Text(segment) : "null"),

            // Where the overlay under the playhead reads from in its own source — the number
            // the audio sync moves, and the only way to assert that it landed.
            ("overlaySourceStart", OverlaySourceStart(model) is { } start ? Text(start) : "null"),

            // Which overlay is picked out on the strip, and where on the timeline the one in
            // question begins and ends — the numbers a drag moves and a trim pulls.
            ("selectedOverlay", model.SelectedOverlay is { } selected ? Text(selected) : "null"),
            ("overlayStart", OverlayRange(model) is { } from ? Text(from.Start) : "null"),
            ("overlayEnd", OverlayRange(model) is { } to ? Text(to.End) : "null"),

            // How long the clip being placed is, settled by the choice of content and not by
            // where it is aimed — the number the clamping exists to protect.
            ("overlayLength",
                model.PendingOverlayContent is { } content ? Text(content.LengthFrames) : "null"),

            ("muted", model.IsMuted ? "true" : "false"),
            ("canUndo", model.CanUndo ? "true" : "false"),
            ("status", Quote(model.Status)),
        };

        return "{" + string.Join(",", fields.Select(f => $"{Quote(f.Name)}:{f.Value}")) + "}";
    });

    /// <summary>
    /// The source in-point of the overlay under the playhead, or of one being placed.
    /// </summary>
    /// <remarks>
    /// Placement mode is checked first, because while an overlay is being positioned the
    /// pending value is the one the sync key writes and the committed list does not yet
    /// contain it.
    /// </remarks>
    private static long? OverlaySourceStart(EditorViewModel model)
    {
        if (model.Mode == EditorMode.Overlay) return model.PendingOverlaySourceStart;

        // Selection first, on the same reasoning as OverlayRange below: a clip that has been
        // pointed at is the one being asked about, and trimming its front takes it out from
        // under the playhead.
        return (model.SelectedOverlayClip ?? OverlayAtPlayhead(model))?.SourceStartFrame;
    }

    /// <summary>
    /// What the overlay in question covers on the timeline.
    /// </summary>
    /// <remarks>
    /// Placement mode first, for the same reason as <see cref="OverlaySourceStart"/> above: the
    /// span a pending overlay would cover is the one being asked about, and it is not in the
    /// document yet. Then the selected clip, because a drag is asked about by selecting it and
    /// the playhead does not follow it along the strip — a trim of the front end moves it out
    /// from under the playhead as often as not. Otherwise the one under the playhead, which is
    /// what every other overlay command in this harness means by "the overlay".
    /// </remarks>
    private static FrameRange? OverlayRange(EditorViewModel model) =>
        model.Mode == EditorMode.Overlay
            ? model.PendingRange
            : (model.SelectedOverlayClip ?? OverlayAtPlayhead(model))?.Range;

    private static OverlayClip? OverlayAtPlayhead(EditorViewModel model)
    {
        foreach (var overlay in model.Project.Overlays)
            if (overlay.Range.Contains(model.Playhead))
                return overlay;

        return null;
    }

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

    /// <summary>
    /// Which mode the editor is in: Normal, Crop, Overlay, or OverlaySource.
    /// </summary>
    /// <remarks>
    /// The source card is a mode, so this is what says it is up — and, more usefully, what
    /// says a choice has been taken and the clip is now being aimed.
    /// </remarks>
    private void AssertMode(string rest)
    {
        var expected = Require(rest, "assert-mode");
        var actual = session.Dispatcher.Invoke(() => session.Model.Mode).ToString();

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new AssertionException($"expected {expected} mode, got {actual}.");
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
    /// Asserts where the overlay under the playhead reads from in its own source.
    /// </summary>
    /// <remarks>
    /// Bounded rather than exact, because the audio sync answers to within a frame or so and
    /// pinning it to a single number would make the assertion about the encoder's priming
    /// samples rather than about the alignment.
    /// </remarks>
    private void AssertOverlaySourceStart(string rest)
    {
        var (lowText, highText) = Split(rest);
        var low = Number(lowText, "assert-overlay-source-start");
        var high = highText.Length == 0 ? low : Number(highText, "assert-overlay-source-start");

        var actual = session.Dispatcher.Invoke(() => OverlaySourceStart(session.Model))
            ?? throw new AssertionException("there is no overlay under the playhead.");

        if (actual < low || actual > high)
            throw new AssertionException(
                $"expected the overlay to start in source frames [{low}, {high}], got {actual}.");
    }

    private void AssertOverlaySelected(string rest)
    {
        var actual = session.Dispatcher.Invoke(() => session.Model.SelectedOverlay)
            ?? throw new AssertionException("no overlay is selected.");

        if (rest.Length == 0) return;

        var expected = Number(rest, "assert-overlay-selected");

        if (actual != expected)
            throw new AssertionException($"expected overlay {expected} to be selected, got {actual}.");
    }

    private void AssertNoOverlaySelected()
    {
        var actual = session.Dispatcher.Invoke(() => session.Model.SelectedOverlay);

        if (actual is not null)
            throw new AssertionException($"expected no selection, overlay {actual} is selected.");
    }

    /// <summary>Asserts where the selected overlay — or the one under the playhead — begins.</summary>
    /// <remarks>
    /// Takes a range like the source-start assertion, because a drag is expressed in pixels
    /// and lands on whichever frame that column covers. A drag asserted to a single frame
    /// would be an assertion about the width of the window.
    /// </remarks>
    private void AssertOverlayStart(string rest) =>
        AssertOverlayEdge(rest, "assert-overlay-start", start: true);

    private void AssertOverlayEnd(string rest) =>
        AssertOverlayEdge(rest, "assert-overlay-end", start: false);

    private void AssertOverlayEdge(string rest, string verb, bool start)
    {
        var (lowText, highText) = Split(rest);
        var low = Number(lowText, verb);
        var high = highText.Length == 0 ? low : Number(highText, verb);

        var range = session.Dispatcher.Invoke(() => OverlayRange(session.Model))
            ?? throw new AssertionException("there is no overlay selected or under the playhead.");

        var actual = start ? range.Start : range.End;

        if (actual < low || actual > high)
            throw new AssertionException(
                $"expected the overlay to {(start ? "start" : "end")} in [{low}, {high}], got {actual}.");
    }

    private void AssertOverlays(string rest)
    {
        var expected = Number(rest, "assert-overlays");
        var actual = session.Dispatcher.Invoke(() => session.Model.Project.Overlays.Length);

        if (actual != expected)
            throw new AssertionException($"expected {expected} overlays, got {actual}.");
    }

    /// <summary>
    /// Where the in and out marks are, or that there are none.
    /// </summary>
    /// <remarks>
    /// Marks are worth asserting on their own because placing an overlay deliberately leaves
    /// them alone — it neither reads them nor spends them — and nothing else about the
    /// document would show it if that stopped being true.
    /// </remarks>
    private void AssertMarks(string rest)
    {
        var (markIn, markOut) = session.Dispatcher.Invoke(
            () => (session.Model.MarkIn, session.Model.MarkOut));

        if (rest.Length == 0)
        {
            if (markIn is not null || markOut is not null)
                throw new AssertionException($"expected no marks, got in {markIn}, out {markOut}.");

            return;
        }

        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) throw new AssertionException("assert-marks needs an in and an out.");

        var expectedIn = Number(parts[0], "assert-marks");
        var expectedOut = Number(parts[1], "assert-marks");

        if (markIn != expectedIn || markOut != expectedOut)
            throw new AssertionException(
                $"expected marks in {expectedIn}, out {expectedOut}, got in {markIn}, out {markOut}.");
    }

    private void AssertSegments(string rest)
    {
        var expected = Number(rest, "assert-segments");
        var actual = session.Dispatcher.Invoke(() => session.Model.Project.Base.Length);

        if (actual != expected)
            throw new AssertionException($"expected {expected} base segments, got {actual}.");
    }

    private void AssertSegmentSelected(string rest)
    {
        var actual = session.Dispatcher.Invoke(() => session.Model.SelectedSegment)
            ?? throw new AssertionException("no segment is selected.");

        if (rest.Length == 0) return;

        var expected = Number(rest, "assert-segment-selected");

        if (actual != expected)
            throw new AssertionException($"expected segment {expected} to be selected, got {actual}.");
    }

    private void AssertNoSegmentSelected()
    {
        var actual = session.Dispatcher.Invoke(() => session.Model.SelectedSegment);

        if (actual is not null)
            throw new AssertionException($"expected no selection, segment {actual} is selected.");
    }

    private void AssertMuted(string rest, bool expected)
    {
        var actual = session.Dispatcher.Invoke(() => session.Model.IsMuted);

        if (actual != expected)
            throw new AssertionException(
                $"expected the preview to be {(expected ? "muted" : "unmuted")}, it is not.");
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

    private void AssertNoMedia()
    {
        if (session.Dispatcher.Invoke(() => session.Model.HasMedia))
            throw new AssertionException("a video is still open.");
    }

    /// <summary>
    /// Asserts this process has let go of a file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only honest test of "the handle was released" is to take the file exclusively and
    /// see whether Windows allows it. <see cref="FileShare.None"/> is what makes it a real
    /// question: the decoder opens its files shared for reading, so a check that asked only
    /// for read access would pass while the video was still very much open.
    /// </para>
    /// <para>
    /// Opened for reading rather than writing so that the assertion never modifies the file
    /// it is asking about, and so it works on a read-only source.
    /// </para>
    /// </remarks>
    private void AssertUnlocked(string rest)
    {
        var path = Resolve(Require(rest, "assert-unlocked"));

        if (!File.Exists(path))
            throw new AssertionException($"there is no file at '{path}' to check.");

        try
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (IOException e)
        {
            throw new AssertionException($"'{path}' is still held open: {e.Message}");
        }
    }

    /// <summary>
    /// Empties the editor, the way the toolbar's close button does.
    /// </summary>
    /// <remarks>
    /// Through the window rather than straight to <c>CloseAll</c>, because releasing the
    /// files is only half of it — the window also has to let go of the bitmap holding the
    /// last frame, and a test that skipped that would pass while the picture was still on
    /// screen. The confirmation prompt is not on this path: it is a modal owned by the
    /// desktop, and it would appear on the user's screen wherever this window is parked.
    /// </remarks>
    private void Close()
    {
        session.Dispatcher.Invoke(session.Window.ClearWorkspace);
        session.Settle();
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
