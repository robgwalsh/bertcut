using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using BertCut.Core.Edits;
using BertCut.Core.Export;
using BertCut.Core.Input;
using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Core.Session;
using BertCut.Core.Time;
using BertCut.Core.Timeline;
using BertCut.Media;
using BertCut.Media.Decode;

namespace BertCut.App;

/// <summary>
/// Editor state and the operations the key map dispatches to.
/// </summary>
/// <remarks>
/// Owned entirely by the UI thread. Playback advances from
/// <c>CompositionTarget.Rendering</c> rather than a timer, so frames are produced in step
/// with WPF's own compositor instead of racing it.
/// </remarks>
public sealed class EditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly FfmpegRuntime _runtime;
    private readonly Dictionary<int, SourceIndex> _indices = [];
    private readonly DispatcherTimer _autosave;
    private readonly Stopwatch _clock = new();

    private EditorDocument _document = new(Project.Empty(new OutputFormat(1280, 720, Rational.FromInt(30))));
    private TimelineResolver _resolver;
    private PreviewEngine? _preview;
    private string? _sessionKey;

    private EditorMode _mode = EditorMode.Normal;
    private RectI _pendingRect;
    private long _playhead;
    private long? _markIn;
    private long? _markOut;
    private int _shuttleRate;
    private long _playbackStartFrame;
    private string _status = "Press Ctrl+O to open a video.";
    private bool _isBusy;

    public EditorViewModel(FfmpegRuntime runtime)
    {
        _runtime = runtime;
        _resolver = new TimelineResolver(_document.Current);

        _document.Changed += OnDocumentChanged;

        // Debounced so a burst of edits writes once. Autosave is the only persistence
        // this app has, so it must be frequent enough to trust and cheap enough to ignore.
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _autosave.Tick += (_, _) => { _autosave.Stop(); SaveSession(); };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the rendered frame changed and the surface should repaint.</summary>
    public event Action? FrameChanged;

    /// <summary>
    /// Raised when opening a video brought last session's edits back with it, carrying the
    /// video's file name.
    /// </summary>
    /// <remarks>
    /// An event rather than a status string because the shell says this twice, in two
    /// registers: once in the status bar, where it scrolls past, and once over the picture,
    /// where it has to be dismissed. The view model owns the fact; the window decides how
    /// loudly to say it.
    /// </remarks>
    public event Action<string>? SessionRestored;

    public Project Project => _document.Current;

    public PreviewEngine? Preview => _preview;

    public EditorMode Mode
    {
        get => _mode;
        private set
        {
            if (_mode == value) return;

            _mode = value;
            Notify();
            Notify(nameof(IsPlacing));
        }
    }

    /// <summary>True while a crop or overlay rectangle is being positioned.</summary>
    public bool IsPlacing => Mode != EditorMode.Normal;

    /// <summary>The rectangle being positioned, in output-space pixels.</summary>
    public RectI PendingRect
    {
        get => _pendingRect;
        private set
        {
            _pendingRect = value;
            Notify();
            RenderCurrentFrame();
        }
    }

    /// <summary>The timeline range the pending rectangle will apply to.</summary>
    public FrameRange PendingRange { get; private set; }

    /// <summary>Which source the pending overlay draws from.</summary>
    public int PendingOverlaySourceId { get; private set; }

    /// <summary>Where in that source the pending overlay starts.</summary>
    public long PendingOverlaySourceStart { get; private set; }

    /// <summary>
    /// The source a new overlay will be taken from — the most recently imported file, or
    /// the base video itself when nothing else has been imported.
    /// </summary>
    public int OverlaySourceId { get; private set; }

    public string OverlaySourceName =>
        Project.FindSource(OverlaySourceId) is { } source ? Path.GetFileName(source.Path) : "—";

    public long DurationFrames => Project.DurationFrames;

    public bool HasMedia => !Project.Base.IsEmpty;

    public long Playhead
    {
        get => _playhead;
        private set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, DurationFrames - 1));
            if (_playhead == clamped) return;

            _playhead = clamped;
            Notify();
            Notify(nameof(TimecodeText));
            RenderCurrentFrame();
        }
    }

    public long? MarkIn { get => _markIn; private set { _markIn = value; Notify(); Notify(nameof(SelectionText)); } }

    public long? MarkOut { get => _markOut; private set { _markOut = value; Notify(); Notify(nameof(SelectionText)); } }

    /// <summary>Playback speed: 0 paused, negative for reverse.</summary>
    public int ShuttleRate
    {
        get => _shuttleRate;
        private set
        {
            _shuttleRate = value;
            Notify();
            Notify(nameof(TransportText));
            Notify(nameof(TransportGlyph));
        }
    }

    public string Status { get => _status; private set { _status = value; Notify(); } }

    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Notify(); } }

    public string TimecodeText => Format(Playhead) + "  /  " + Format(DurationFrames);

    public string TransportText => ShuttleRate switch
    {
        0 => "Paused",
        1 => "Playing",
        > 1 => $"Forward {ShuttleRate}x",
        -1 => "Reverse",
        _ => $"Reverse {-ShuttleRate}x",
    };

    /// <summary>
    /// Transport state as a glyph for the status bar.
    /// </summary>
    /// <remarks>
    /// A cross when stopped, otherwise a chevron pointing the way the playhead is moving —
    /// one per doubling, so 8x is four of them. The count is the readable part: at a glance
    /// the difference between one chevron and three is obvious in a way that "2x" and "8x"
    /// in the same small type is not.
    /// </remarks>
    public string TransportGlyph
    {
        get
        {
            if (ShuttleRate == 0) return "✕";

            var chevrons = BitOperations.Log2((uint)Math.Abs(ShuttleRate)) + 1;
            return new string(ShuttleRate > 0 ? '❯' : '❮', chevrons);
        }
    }

    /// <summary>True while the playhead is not moving, which is what colours the glyph.</summary>
    public bool IsStopped => ShuttleRate == 0;

    public string SelectionText
    {
        get
        {
            if (MarkIn is null && MarkOut is null) return "No selection";
            if (SelectedRange is { } range)
                return $"In {Format(range.Start)}  Out {Format(range.End)}  ({Format(range.Length)})";
            return MarkIn is not null ? $"In {Format(MarkIn.Value)}" : $"Out {Format(MarkOut!.Value)}";
        }
    }

    /// <summary>
    /// The marked range, defaulting the missing end to the playhead or the timeline end.
    /// </summary>
    /// <remarks>
    /// Marking only an in-point and pressing X is a common shortcut — it should delete
    /// from there to the playhead rather than refusing.
    /// </remarks>
    public FrameRange? SelectedRange
    {
        get
        {
            var start = MarkIn ?? (MarkOut is not null ? Playhead : null);
            var end = MarkOut ?? (MarkIn is not null ? Playhead : null);
            if (start is null || end is null) return null;

            var range = new FrameRange(Math.Min(start.Value, end.Value), Math.Max(start.Value, end.Value));
            return range.IsEmpty ? null : range.ClampTo(DurationFrames);
        }
    }

    public bool CanUndo => _document.CanUndo;

    public bool CanRedo => _document.CanRedo;

    public SourceIndex IndexOf(int sourceId) => _indices[sourceId];

    // ---- media -------------------------------------------------------------------

    public async Task OpenAsync(string path)
    {
        IsBusy = true;
        Status = $"Reading {Path.GetFileName(path)}...";

        try
        {
            var probe = await new MediaProber(_runtime).ProbeAsync(path);
            _indices[1] = probe.Index;

            var output = new OutputFormat(probe.Media.Width, probe.Media.Height, probe.Media.FrameRate);
            var fresh = TimelineEdits.ImportSource(Project.Empty(output), probe.Media);

            _sessionKey = probe.Media.ContentKey;

            // Reopening a video silently restores the edits made to it last time. The
            // restore is pushed onto the undo stack, so Ctrl+Z discards it — which is why
            // this needs no "restore or start fresh?" prompt.
            var restored = SessionStore.TryLoad(_sessionKey);
            var usable = restored is not null && SourcesStillResolve(restored);

            _document.Changed -= OnDocumentChanged;
            _document = new EditorDocument(fresh, "Open video");
            _document.Changed += OnDocumentChanged;

            _preview?.Dispose();
            _preview = new PreviewEngine(
                output,
                id => _indices[id],
                id => _document.Current.RequireSource(id).Path);

            if (usable)
            {
                foreach (var source in restored!.Sources)
                    if (!_indices.ContainsKey(source.Id))
                        _indices[source.Id] = (await new MediaProber(_runtime).ProbeAsync(source.Path)).Index;

                _document.Replace("Restore session", restored);
            }

            _playhead = 0;
            MarkIn = null;
            MarkOut = null;
            Mode = EditorMode.Normal;

            // With nothing else imported, an overlay comes from the base video itself —
            // a zoomed inset of one moment shown over the full view.
            OverlaySourceId = _document.Current.Sources[0].Id;
            Notify(nameof(OverlaySourceName));

            OnDocumentChanged(_document.Current);

            Status = usable
                ? $"{Path.GetFileName(path)} — restored your previous edits (Ctrl+Z to discard)"
                : Describe(probe.Media);

            if (usable) SessionRestored?.Invoke(Path.GetFileName(path));
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FfmpegDecodeException)
        {
            Status = $"Could not open {Path.GetFileName(path)}: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Imports an additional video, available as an overlay source.
    /// </summary>
    /// <remarks>
    /// It is not appended to the base track — importing a webcam recording should not
    /// lengthen the timeline, it should give you something to put on top of it.
    /// </remarks>
    public async Task ImportAsync(string path)
    {
        if (!HasMedia)
        {
            await OpenAsync(path);
            return;
        }

        IsBusy = true;

        try
        {
            var probe = await new MediaProber(_runtime).ProbeAsync(path);

            var nextId = Project.Sources.Max(s => s.Id) + 1;
            _indices[nextId] = probe.Index;

            Apply($"Import {Path.GetFileName(path)}",
                p => TimelineEdits.ImportSource(p, probe.Media, appendToBase: false));

            OverlaySourceId = nextId;
            Notify(nameof(OverlaySourceName));

            Status = $"{Path.GetFileName(path)} imported — press P over a marked range to overlay it";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FfmpegDecodeException)
        {
            Status = $"Could not import {Path.GetFileName(path)}: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Adds a video to the end of the base track, as a new segment.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="ImportAsync"/>: importing gives you something to put
    /// on top of the timeline, appending makes it part of the timeline. Its length is
    /// converted into output frames on the way in, so joining a 60 fps capture onto a
    /// 30 fps recording cannot drift; its picture is scaled to the project's frame, by the
    /// preview and the export alike.
    /// </remarks>
    public async Task AppendAsync(string path)
    {
        if (!HasMedia)
        {
            await OpenAsync(path);
            return;
        }

        IsBusy = true;
        Status = $"Reading {Path.GetFileName(path)}...";

        try
        {
            var probe = await new MediaProber(_runtime).ProbeAsync(path);

            var nextId = Project.Sources.Max(s => s.Id) + 1;
            _indices[nextId] = probe.Index;

            var join = DurationFrames;

            Apply($"Add {Path.GetFileName(path)}",
                p => TimelineEdits.ImportSource(p, probe.Media, appendToBase: true));

            if (DurationFrames == join)
            {
                Status = $"{Path.GetFileName(path)} has no frames to add.";
                return;
            }

            // Land on the join: that is the frame the user wants to look at, and the one
            // they are most likely to trim from.
            _playhead = join;
            MarkIn = null;
            MarkOut = null;

            Notify(nameof(Playhead));
            Notify(nameof(TimecodeText));
            RenderCurrentFrame();

            var stretched = probe.Media.Width * Project.Output.Height
                            != Project.Output.Width * probe.Media.Height
                ? $" — {probe.Media.Width}x{probe.Media.Height} stretched to the project's " +
                  $"{Project.Output.Width}x{Project.Output.Height} frame"
                : "";

            Status = $"{Path.GetFileName(path)} added to the end{stretched} — Ctrl+Z to undo";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FfmpegDecodeException)
        {
            Status = $"Could not add {Path.GetFileName(path)}: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Throws away every edit and returns to the video as it was opened.
    /// </summary>
    /// <remarks>
    /// Goes through the undo stack like any other edit, so the confirmation prompt in front
    /// of it is about intent rather than about anything being unrecoverable — Ctrl+Z brings
    /// the whole edit back.
    /// </remarks>
    public void ResetAll()
    {
        if (!HasMedia)
        {
            Status = "Nothing to reset.";
            return;
        }

        // The first source is the video the session is keyed to; everything else was
        // imported or appended on top of it and goes away with the edits.
        var original = Project.Sources[0];
        var output = new OutputFormat(
            original.Width, original.Height, original.FrameRate, Project.Output.SampleRate);

        var fresh = TimelineEdits.ImportSource(Project.Empty(output), original);

        // ImportSource renumbers from scratch, so the index has to follow the new id.
        var id = fresh.Sources[0].Id;
        if (!_indices.ContainsKey(id)) _indices[id] = _indices[original.Id];

        _document.Replace("Reset everything", fresh);

        _playhead = 0;
        MarkIn = null;
        MarkOut = null;
        Mode = EditorMode.Normal;

        OverlaySourceId = id;
        Notify(nameof(OverlaySourceName));
        Notify(nameof(Playhead));
        Notify(nameof(TimecodeText));

        // Decoders are cached per source, and the sources that were dropped here will
        // never be asked for again.
        _preview?.Reset();
        RenderCurrentFrame();

        Status = "Reset to the original video — Ctrl+Z to undo";
    }

    private static string Describe(SourceMedia media)
    {
        var vfr = media.IsVariableFrameRate ? "  ⚠ variable frame rate" : "";
        return $"{Path.GetFileName(media.Path)} — {media.Width}x{media.Height} " +
               $"{media.FrameRate.Approx:0.##} fps, {media.FrameCount} frames{vfr}";
    }

    private bool SourcesStillResolve(Project project) =>
        project.Sources.All(s => File.Exists(s.Path));

    // ---- intents -----------------------------------------------------------------

    public void Dispatch(EditorIntent intent)
    {
        switch (intent)
        {
            case EditorIntent.PlayPause: ShuttleRate = ShuttleRate == 0 ? 1 : 0; RestartClock(); break;
            case EditorIntent.Stop: ShuttleRate = 0; break;
            case EditorIntent.ShuttleForward: ShuttleRate = ShuttleRate < 1 ? 1 : Math.Min(8, ShuttleRate * 2); RestartClock(); break;
            case EditorIntent.ShuttleReverse: ShuttleRate = ShuttleRate > -1 ? -1 : Math.Max(-8, ShuttleRate * 2); RestartClock(); break;

            case EditorIntent.StepBack: ShuttleRate = 0; Playhead--; break;
            case EditorIntent.StepForward: ShuttleRate = 0; Playhead++; break;
            case EditorIntent.StepBackSecond: ShuttleRate = 0; Playhead -= FramesPerSecond; break;
            case EditorIntent.StepForwardSecond: ShuttleRate = 0; Playhead += FramesPerSecond; break;
            case EditorIntent.GoToStart: ShuttleRate = 0; Playhead = 0; break;
            case EditorIntent.GoToEnd: ShuttleRate = 0; Playhead = DurationFrames - 1; break;
            case EditorIntent.PreviousEdit: ShuttleRate = 0; Playhead = AdjacentBoundary(-1); break;
            case EditorIntent.NextEdit: ShuttleRate = 0; Playhead = AdjacentBoundary(1); break;

            case EditorIntent.MarkIn: MarkIn = Playhead; break;
            case EditorIntent.MarkOut: MarkOut = Playhead; break;
            case EditorIntent.ClearMarks: MarkIn = null; MarkOut = null; break;

            case EditorIntent.RippleDelete: RippleDelete(); break;
            case EditorIntent.SplitAtPlayhead: Apply($"Split at {Format(Playhead)}", p => TimelineEdits.SplitAt(p, Playhead)); break;
            case EditorIntent.ClearCropAtPlayhead: ClearCropAtPlayhead(); break;
            case EditorIntent.RemoveOverlayAtPlayhead:
                Apply("Remove overlay", p => TimelineEdits.RemoveOverlayAt(p, Playhead));
                break;

            case EditorIntent.BeginCrop: BeginCrop(); break;
            case EditorIntent.BeginOverlay: BeginOverlay(); break;
            case EditorIntent.Commit: CommitPlacement(); break;
            case EditorIntent.Cancel: CancelPlacement(); break;

            case EditorIntent.NudgeLeft: Nudge(-NudgeStep, 0); break;
            case EditorIntent.NudgeRight: Nudge(NudgeStep, 0); break;
            case EditorIntent.NudgeUp: Nudge(0, -NudgeStep); break;
            case EditorIntent.NudgeDown: Nudge(0, NudgeStep); break;
            case EditorIntent.Grow: Resize(1.1); break;
            case EditorIntent.Shrink: Resize(1 / 1.1); break;

            case EditorIntent.SnapTopLeft: SnapTo(Anchor.TopLeft); break;
            case EditorIntent.SnapTopRight: SnapTo(Anchor.TopRight); break;
            case EditorIntent.SnapBottomLeft: SnapTo(Anchor.BottomLeft); break;
            case EditorIntent.SnapBottomRight: SnapTo(Anchor.BottomRight); break;
            case EditorIntent.SnapCenter: SnapTo(Anchor.Centre); break;

            case EditorIntent.TrimOverlayBack: TrimOverlay(-1); break;
            case EditorIntent.TrimOverlayForward: TrimOverlay(1); break;

            case EditorIntent.ToggleOverlayMute: ToggleOverlayMute(); break;

            case EditorIntent.Undo: if (_document.Undo()) Status = "Undone"; break;
            case EditorIntent.Redo: if (_document.Redo()) Status = "Redone"; break;
        }
    }

    /// <summary>Pixels a single arrow press moves the pending rectangle.</summary>
    private const int NudgeStep = 8;

    // ---- placing a crop or an overlay ---------------------------------------------

    private void BeginCrop()
    {
        if (!HasMedia)
        {
            Status = "Press Ctrl+O to open a video.";
            return;
        }

        // With nothing marked, the crop covers the whole video. Reframing an entire
        // recording is common enough that it should not first require marking it end to
        // end, and unlike a ripple delete there is nothing destructive about the default.
        var selection = SelectedRange;
        var range = selection ?? new FrameRange(0, DurationFrames);

        if (range.IsEmpty)
        {
            Status = "Nothing to crop.";
            return;
        }

        ShuttleRate = 0;
        PendingRange = range;

        // Locked to the output ratio, so zoom-to-fill never has to letterbox.
        var existing = Project.Crops.FirstOrDefault(c => c.Range.Overlaps(range));
        PendingRect = existing.Rect.W > 0
            ? existing.Rect
            : RectPlacement.Initial(
                Project.Output.Width, Project.Output.Height,
                Project.Output.Width, Project.Output.Height,
                fraction: 0.6, Anchor.Centre);

        Mode = EditorMode.Crop;
        Status = (selection is null ? "Cropping the whole video · " : "")
                 + "Drag the box or its corners · arrows move · Shift+↑/↓ resize · "
                 + "1-5 snap · Enter applies · Esc cancels";
    }

    private void BeginOverlay()
    {
        if (SelectedRange is not { } range)
        {
            Status = "Mark the range to overlay with I and O first.";
            return;
        }

        if (Project.FindSource(OverlaySourceId) is not { } source)
        {
            Status = "Ctrl+I to import a video to overlay.";
            return;
        }

        ShuttleRate = 0;
        PendingRange = range;
        PendingOverlaySourceId = OverlaySourceId;

        // Overlaying the base video on itself is a zoomed inset of a moment, so it starts
        // from what is already on screen; a separate file starts from its own beginning.
        PendingOverlaySourceStart = source.Id == Project.Base[0].SourceId
            ? _resolver.Resolve(range.Start)?.SourceFrame ?? 0
            : 0;

        // Locked to the overlay source's own ratio, so its picture is never stretched.
        PendingRect = RectPlacement.Initial(
            Project.Output.Width, Project.Output.Height,
            source.Width, source.Height,
            fraction: 0.3, Anchor.BottomRight);

        PendingRect = RectPlacement.Snap(
            PendingRect, Project.Output.Width, Project.Output.Height, Anchor.BottomRight,
            margin: Project.Output.Width / 50);

        Mode = EditorMode.Overlay;
        Status = $"Overlaying {Path.GetFileName(source.Path)} · arrows move · Shift+↑/↓ resize · " +
                 "1-5 snap · Alt+←/→ sync · Enter places · Esc cancels";
    }

    private void CommitPlacement()
    {
        switch (Mode)
        {
            case EditorMode.Crop:
                var range = PendingRange;
                var rect = PendingRect;
                Apply($"Crop {rect.W}x{rect.H}", p => TimelineEdits.SetCrop(p, range, rect));
                break;

            case EditorMode.Overlay:
                var clip = new OverlayClip(
                    PendingRange, PendingOverlaySourceId, PendingOverlaySourceStart, PendingRect);
                Apply("Place overlay", p => TimelineEdits.AddOverlay(p, clip));
                break;

            default:
                return;
        }

        Mode = EditorMode.Normal;
        MarkIn = null;
        MarkOut = null;
        RenderCurrentFrame();
    }

    private void CancelPlacement()
    {
        if (Mode == EditorMode.Normal) return;

        Mode = EditorMode.Normal;
        Status = "Cancelled";
        RenderCurrentFrame();
    }

    /// <summary>Positions the pending rectangle from a mouse drag, in output-space pixels.</summary>
    public void SetPendingRect(RectI rect)
    {
        if (Mode == EditorMode.Normal) return;

        PendingRect = RectPlacement.Clamp(rect, Project.Output.Width, Project.Output.Height);
    }

    /// <summary>Builds an aspect-locked rectangle from a freehand drag.</summary>
    public void DragPendingRect(int x0, int y0, int x1, int y1)
    {
        if (Mode == EditorMode.Normal) return;

        var (aspectW, aspectH) = PendingAspect();
        PendingRect = RectPlacement.FromDrag(
            x0, y0, x1, y1, aspectW, aspectH, Project.Output.Width, Project.Output.Height);
    }

    /// <summary>Resizes the pending rectangle from a dragged corner handle.</summary>
    public void ResizePendingRect(int anchorX, int anchorY, int x, int y)
    {
        if (Mode == EditorMode.Normal) return;

        var (aspectW, aspectH) = PendingAspect();
        PendingRect = RectPlacement.FromCorner(
            anchorX, anchorY, x, y, aspectW, aspectH, Project.Output.Width, Project.Output.Height);
    }

    private (int Width, int Height) PendingAspect() =>
        Mode == EditorMode.Crop
            ? (Project.Output.Width, Project.Output.Height)
            : Project.FindSource(PendingOverlaySourceId) is { } source
                ? (source.Width, source.Height)
                : (Project.Output.Width, Project.Output.Height);

    private void Nudge(int dx, int dy)
    {
        if (Mode == EditorMode.Normal) return;
        PendingRect = RectPlacement.Move(PendingRect, dx, dy, Project.Output.Width, Project.Output.Height);
    }

    private void Resize(double factor)
    {
        if (Mode == EditorMode.Normal) return;
        PendingRect = RectPlacement.Resize(PendingRect, factor, Project.Output.Width, Project.Output.Height);
    }

    private void SnapTo(Anchor anchor)
    {
        if (Mode == EditorMode.Normal) return;

        var margin = anchor == Anchor.Centre ? 0 : Project.Output.Width / 50;
        PendingRect = RectPlacement.Snap(
            PendingRect, Project.Output.Width, Project.Output.Height, anchor, margin);
    }

    /// <summary>Slides the pending overlay's content against the base track.</summary>
    private void TrimOverlay(int frames)
    {
        if (Mode != EditorMode.Overlay) return;

        var source = Project.RequireSource(PendingOverlaySourceId);
        var limit = Math.Max(0, source.FrameCount - PendingRange.Length);

        PendingOverlaySourceStart = Math.Clamp(PendingOverlaySourceStart + frames, 0, limit);
        Status = $"Overlay starts at source frame {PendingOverlaySourceStart}";
        RenderCurrentFrame();
    }

    private void ToggleOverlayMute()
    {
        for (var i = 0; i < Project.Overlays.Length; i++)
            if (Project.Overlays[i].Range.Contains(Playhead))
            {
                var index = i;
                var muted = !Project.Overlays[i].Muted;
                Apply(muted ? "Mute overlay" : "Unmute overlay", p => TimelineEdits.ToggleOverlayMute(p, index));
                return;
            }

        Status = "No overlay under the playhead.";
    }

    private int FramesPerSecond => Math.Max(1, (int)Math.Round(Project.Output.FrameRate.Approx));

    private void RippleDelete()
    {
        if (SelectedRange is not { } range)
        {
            Status = "Mark a range with I and O first.";
            return;
        }

        ShuttleRate = 0;
        var label = $"Ripple delete {Format(range.Length)}";

        Apply(label, p => TimelineEdits.RippleDelete(p, range));

        // The playhead follows the cut point, which is where the user's attention is and
        // where they will most likely mark the next cut.
        _playhead = Math.Clamp(range.Start, 0, Math.Max(0, DurationFrames - 1));
        MarkIn = null;
        MarkOut = null;

        Notify(nameof(Playhead));
        Notify(nameof(TimecodeText));
        RenderCurrentFrame();

        Status = $"{label} — Ctrl+Z to undo";
    }

    private void ClearCropAtPlayhead()
    {
        var at = Playhead;
        foreach (var crop in Project.Crops)
            if (crop.Range.Contains(at))
            {
                Apply("Clear crop", p => TimelineEdits.ClearCrop(p, crop.Range));
                return;
            }

        Status = "No crop under the playhead.";
    }

    public void SetCrop(FrameRange range, RectI rect) =>
        Apply("Crop", p => TimelineEdits.SetCrop(p, range, rect));

    public void Apply(string label, Func<Project, Project> edit)
    {
        _document.Apply(label, edit);
        Status = label;
    }

    /// <summary>The nearest segment boundary in <paramref name="direction"/>.</summary>
    private long AdjacentBoundary(int direction)
    {
        var boundaries = Project.Base
            .Select(b => b.TimelineStart)
            .Concat([DurationFrames])
            .Distinct()
            .OrderBy(b => b)
            .ToList();

        return direction < 0
            ? boundaries.LastOrDefault(b => b < Playhead, 0)
            : boundaries.FirstOrDefault(b => b > Playhead, Math.Max(0, DurationFrames - 1));
    }

    // ---- playback ----------------------------------------------------------------

    private void RestartClock()
    {
        _playbackStartFrame = Playhead;
        _clock.Restart();
    }

    /// <summary>
    /// Advances the playhead to match elapsed time. Called from the composition tick.
    /// </summary>
    /// <remarks>
    /// The position is computed from elapsed time rather than incremented per tick, so a
    /// slow frame causes a dropped frame instead of playback drifting behind wall clock.
    /// Once audio is wired in, this clock is replaced by the audio device's own position.
    /// </remarks>
    public void Tick()
    {
        if (ShuttleRate == 0 || !HasMedia) return;

        var elapsed = _clock.Elapsed.TotalSeconds;
        var advance = (long)(elapsed * Project.Output.FrameRate.Approx * ShuttleRate);
        var target = _playbackStartFrame + advance;

        if (target >= DurationFrames)
        {
            Playhead = DurationFrames - 1;
            ShuttleRate = 0;
            return;
        }

        if (target < 0)
        {
            Playhead = 0;
            ShuttleRate = 0;
            return;
        }

        Playhead = target;
    }

    /// <summary>
    /// Runs at normal speed for as long as a step key is held down.
    /// </summary>
    /// <remarks>
    /// Called from the keyboard's auto-repeat rather than from the first keystroke, so a
    /// tap on <c>&lt;</c> or <c>&gt;</c> is one frame and a hold turns into playback.
    /// Re-arming the clock on every repeat would pin the playhead a frame from where it
    /// started, so an already-running hold is left alone.
    /// </remarks>
    public void HoldShuttle(int direction)
    {
        if (ShuttleRate == direction) return;

        ShuttleRate = direction;
        RestartClock();
    }

    /// <summary>Ends the run a held step key started.</summary>
    public void ReleaseShuttle() => ShuttleRate = 0;

    /// <summary>Moves the playhead during a scrub without stopping to decode exactly.</summary>
    public void ScrubTo(long frame)
    {
        ShuttleRate = 0;
        Playhead = frame;
    }

    private void RenderCurrentFrame()
    {
        if (_preview is null || !HasMedia) return;

        try
        {
            if (_preview.Render(PreviewResolver(), _playhead)) FrameChanged?.Invoke();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            Status = $"Preview failed: {e.Message}";
        }
    }

    /// <summary>
    /// The resolver the preview renders through, including any in-progress placement.
    /// </summary>
    /// <remarks>
    /// The two placements want opposite things from the preview. A pending <b>overlay</b>
    /// is composited live, because the whole point of positioning it is seeing where its
    /// picture lands. A pending <b>crop</b> deliberately is not applied: while choosing the
    /// region you need to see the frame you are choosing from, not the zoomed result. The
    /// rectangle is drawn over the preview instead, and the zoom appears on commit.
    /// </remarks>
    private TimelineResolver PreviewResolver()
    {
        if (Mode != EditorMode.Overlay) return _resolver;

        var clip = new OverlayClip(
            PendingRange, PendingOverlaySourceId, PendingOverlaySourceStart, PendingRect);

        return new TimelineResolver(TimelineEdits.AddOverlay(Project, clip));
    }

    private void OnDocumentChanged(Project project)
    {
        // Each consumer builds its own resolver because the hint cache inside one is not
        // shareable; rebuilding is trivial.
        _resolver = new TimelineResolver(project);

        if (_playhead >= project.DurationFrames)
            _playhead = Math.Max(0, project.DurationFrames - 1);

        _preview?.SetOutput(project.Output);

        Notify(nameof(Project));
        Notify(nameof(DurationFrames));
        Notify(nameof(HasMedia));
        Notify(nameof(TimecodeText));
        Notify(nameof(CanUndo));
        Notify(nameof(CanRedo));

        RenderCurrentFrame();

        _autosave.Stop();
        _autosave.Start();
    }

    private void SaveSession()
    {
        if (_sessionKey is null || !HasMedia) return;

        try
        {
            SessionStore.Save(_sessionKey, Project);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = "Autosave failed — check disk space.";
        }
    }

    /// <summary>Forces a save, for window close and before an export.</summary>
    public void FlushSession()
    {
        _autosave.Stop();
        SaveSession();
    }

    public string Format(long frames)
    {
        var rate = Project.Output.FrameRate;
        var (seconds, remainder) = RationalMath.SplitSeconds(frames, rate);
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}:{remainder:00}";
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _autosave.Stop();
        _preview?.Dispose();
    }
}
