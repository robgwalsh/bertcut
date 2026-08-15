namespace BertCut.Core.Input;

/// <summary>
/// Keys BertCut binds, independent of WPF.
/// </summary>
/// <remarks>
/// Core carries its own key enum rather than referencing <c>System.Windows.Input</c> so
/// the key map stays unit-testable and Core stays free of a UI framework dependency. The
/// app translates WPF keys into these at the edge.
/// </remarks>
/// <remarks>
/// The whole keyboard is listed, not just the keys the defaults use: every one of these is
/// a key the user can rebind an action onto from the Controls page, and a key missing here
/// is a key that silently does nothing when they press it.
/// </remarks>
public enum EditorKey
{
    None,
    Space, Left, Right, Up, Down, Home, End, Enter, Escape, Delete, Insert, Tab, PageUp, PageDown,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Minus, Equals, Backslash, Comma, Period, Semicolon, Quote, LeftBracket, RightBracket, Slash, Backtick,
}

[Flags]
public enum EditorModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

/// <summary>What the editor is currently doing, which changes what some keys mean.</summary>
public enum EditorMode
{
    Normal,

    /// <summary>Dragging a crop rectangle; arrows nudge it and Enter commits.</summary>
    Crop,

    /// <summary>Placing a picture-in-picture overlay.</summary>
    Overlay,
}

/// <summary>An action the user asked for, resolved from a keystroke.</summary>
public enum EditorIntent
{
    None,
    PlayPause, ShuttleForward, ShuttleReverse, Stop,
    StepBack, StepForward, StepBackSecond, StepForwardSecond,
    PreviousEdit, NextEdit, GoToStart, GoToEnd,
    MarkIn, MarkOut, ClearMarks,
    RippleDelete, SplitAtPlayhead,
    BeginCrop, ClearCropAtPlayhead,
    BeginOverlay, RemoveOverlayAtPlayhead,

    /// <summary>
    /// No longer bound and no longer acted on: the export mixes the base track's audio and
    /// only that, so muting an overlay never changed anything.
    /// <see cref="ToggleMute"/> took its key and does something a user can hear.
    /// </summary>
    /// <remarks>
    /// The value stays in the enum because <c>OverlayClip.Muted</c> and the session format
    /// still carry the flag, and <c>TimelineEdits.ToggleOverlayMute</c> still writes it. A
    /// customization stored against the old slot is dropped as an unknown id, which is the
    /// same thing that happens to any binding whose default disappears.
    /// </remarks>
    ToggleOverlayMute,

    /// <summary>Silence the preview. Monitoring only — it does not change the export.</summary>
    ToggleMute,
    Commit, Cancel,
    NudgeLeft, NudgeRight, NudgeUp, NudgeDown,

    /// <summary>
    /// Resize the pending rectangle. Both crop and overlay rectangles are aspect-locked,
    /// so resizing is a single dimension — one pair of keys rather than four.
    /// </summary>
    Grow, Shrink,

    SnapTopLeft, SnapTopRight, SnapBottomLeft, SnapBottomRight, SnapCenter,

    /// <summary>Slide the overlay's source in-point, to sync its content against the base.</summary>
    TrimOverlayBack, TrimOverlayForward,

    /// <summary>
    /// Find that same alignment automatically, by correlating the overlay's audio against
    /// the base track's underneath it.
    /// </summary>
    SyncOverlayAudio,

    Undo, Redo,
    OpenFile, ImportSource, Export,

    /// <summary>Add a video to the end of the base track, as opposed to importing one to overlay.</summary>
    AppendSource,

    ZoomIn, ZoomOut, ZoomToFit,
    ToggleHelp, ToggleSettings,
}

/// <summary>One binding, for both dispatch and the on-screen cheat sheet.</summary>
public sealed record KeyBinding(
    EditorKey Key,
    EditorModifiers Modifiers,
    EditorMode Mode,
    EditorIntent Intent,
    string Description);

/// <summary>
/// The keyboard map, as data.
/// </summary>
/// <remarks>
/// <para>
/// Bindings follow Premiere and Resolve conventions where one exists, so muscle memory
/// transfers. Where this editor differs, it differs deliberately: ripple delete is bound
/// to a bare <c>X</c> as well as the conventional <c>Shift+Delete</c>, because it is the
/// overwhelming majority of keystrokes in this tool and must be reachable without leaving
/// the J-K-L home position. Crop gets a bare <c>C</c>, which is free precisely because
/// BertCut has no razor tool — a case where removing a feature bought ergonomics.
/// </para>
/// <para>
/// There is no Ctrl+S. Autosave is the contract, and a Save key would imply it isn't. The
/// toolbar's Save as is a different thing wearing a familiar icon — it writes a finished
/// video out, and never touches the file you opened — and it stays on Ctrl+E for that
/// reason.
/// </para>
/// <para>
/// This map is the <i>defaults</i>. What the app actually dispatches against is a
/// <see cref="KeyBindings"/> built from it, which layers the user's own choices from the
/// Controls page on top — so everything below is a starting point rather than a fact.
/// </para>
/// </remarks>
public static class KeyMap
{
    /// <summary>The bindings BertCut ships with, before any customization.</summary>
    public static readonly IReadOnlyList<KeyBinding> Bindings =
    [
        // Playback and navigation
        new(EditorKey.Space, EditorModifiers.None, EditorMode.Normal, EditorIntent.PlayPause, "Play / pause"),
        new(EditorKey.L, EditorModifiers.None, EditorMode.Normal, EditorIntent.ShuttleForward, "Shuttle forward (press again for 2x, 4x, 8x)"),
        new(EditorKey.J, EditorModifiers.None, EditorMode.Normal, EditorIntent.ShuttleReverse, "Shuttle reverse"),
        new(EditorKey.K, EditorModifiers.None, EditorMode.Normal, EditorIntent.Stop, "Stop"),
        new(EditorKey.Left, EditorModifiers.None, EditorMode.Normal, EditorIntent.StepBack, "Back one frame"),
        new(EditorKey.Right, EditorModifiers.None, EditorMode.Normal, EditorIntent.StepForward, "Forward one frame"),
        new(EditorKey.Comma, EditorModifiers.None, EditorMode.Normal, EditorIntent.StepBack, "Back one frame — hold to run backwards"),
        new(EditorKey.Period, EditorModifiers.None, EditorMode.Normal, EditorIntent.StepForward, "Forward one frame — hold to run forwards"),
        new(EditorKey.Left, EditorModifiers.Shift, EditorMode.Normal, EditorIntent.StepBackSecond, "Back one second"),
        new(EditorKey.Right, EditorModifiers.Shift, EditorMode.Normal, EditorIntent.StepForwardSecond, "Forward one second"),
        new(EditorKey.Up, EditorModifiers.None, EditorMode.Normal, EditorIntent.PreviousEdit, "Previous cut boundary"),
        new(EditorKey.Down, EditorModifiers.None, EditorMode.Normal, EditorIntent.NextEdit, "Next cut boundary"),
        new(EditorKey.Home, EditorModifiers.None, EditorMode.Normal, EditorIntent.GoToStart, "Go to start"),
        new(EditorKey.End, EditorModifiers.None, EditorMode.Normal, EditorIntent.GoToEnd, "Go to end"),

        // Marking and the three operations
        new(EditorKey.I, EditorModifiers.None, EditorMode.Normal, EditorIntent.MarkIn, "Mark in"),
        new(EditorKey.O, EditorModifiers.None, EditorMode.Normal, EditorIntent.MarkOut, "Mark out"),
        new(EditorKey.Escape, EditorModifiers.None, EditorMode.Normal, EditorIntent.ClearMarks, "Clear in / out"),
        new(EditorKey.X, EditorModifiers.None, EditorMode.Normal, EditorIntent.RippleDelete, "Ripple delete the marked range"),
        new(EditorKey.Delete, EditorModifiers.Shift, EditorMode.Normal, EditorIntent.RippleDelete, "Ripple delete the marked range"),
        new(EditorKey.S, EditorModifiers.None, EditorMode.Normal, EditorIntent.SplitAtPlayhead, "Split at the playhead"),
        new(EditorKey.C, EditorModifiers.None, EditorMode.Normal, EditorIntent.BeginCrop, "Crop the marked range, or the whole video if nothing is marked"),
        new(EditorKey.C, EditorModifiers.Shift, EditorMode.Normal, EditorIntent.ClearCropAtPlayhead, "Clear the crop under the playhead"),
        new(EditorKey.P, EditorModifiers.None, EditorMode.Normal, EditorIntent.BeginOverlay, "Place an overlay over the marked range"),
        new(EditorKey.P, EditorModifiers.Shift, EditorMode.Normal, EditorIntent.RemoveOverlayAtPlayhead, "Remove the overlay under the playhead"),
        new(EditorKey.A, EditorModifiers.None, EditorMode.Normal, EditorIntent.SyncOverlayAudio, "Sync the overlay to the base track by its sound"),
        new(EditorKey.M, EditorModifiers.None, EditorMode.Normal, EditorIntent.ToggleMute, "Mute / unmute the preview"),

        // Crop and overlay placement share their editing keys, so the gesture for putting
        // a rectangle somewhere is the same one in both modes.
        .. PlacementBindings(EditorMode.Crop, "Apply the crop"),
        .. PlacementBindings(EditorMode.Overlay, "Place the overlay"),

        // Only overlays have a source to slide against the base track.
        new(EditorKey.Left, EditorModifiers.Alt, EditorMode.Overlay, EditorIntent.TrimOverlayBack, "Overlay content back one frame"),
        new(EditorKey.Right, EditorModifiers.Alt, EditorMode.Overlay, EditorIntent.TrimOverlayForward, "Overlay content forward one frame"),
        new(EditorKey.A, EditorModifiers.None, EditorMode.Overlay, EditorIntent.SyncOverlayAudio, "Sync to the base track by sound"),

        // Document and view
        new(EditorKey.Z, EditorModifiers.Control, EditorMode.Normal, EditorIntent.Undo, "Undo"),
        new(EditorKey.Z, EditorModifiers.Control | EditorModifiers.Shift, EditorMode.Normal, EditorIntent.Redo, "Redo"),
        new(EditorKey.Y, EditorModifiers.Control, EditorMode.Normal, EditorIntent.Redo, "Redo"),
        new(EditorKey.O, EditorModifiers.Control, EditorMode.Normal, EditorIntent.OpenFile, "Open a video"),
        new(EditorKey.I, EditorModifiers.Control, EditorMode.Normal, EditorIntent.ImportSource, "Import another video to overlay"),
        new(EditorKey.A, EditorModifiers.Control, EditorMode.Normal, EditorIntent.AppendSource, "Add a video to the end of the timeline"),
        new(EditorKey.E, EditorModifiers.Control, EditorMode.Normal, EditorIntent.Export, "Save as — write the edited video to a new file"),
        new(EditorKey.Equals, EditorModifiers.None, EditorMode.Normal, EditorIntent.ZoomIn, "Zoom in"),
        new(EditorKey.Minus, EditorModifiers.None, EditorMode.Normal, EditorIntent.ZoomOut, "Zoom out"),
        new(EditorKey.Backslash, EditorModifiers.None, EditorMode.Normal, EditorIntent.ZoomToFit, "Zoom to fit"),
        new(EditorKey.F1, EditorModifiers.None, EditorMode.Normal, EditorIntent.ToggleHelp, "Show / hide shortcuts"),
        new(EditorKey.Comma, EditorModifiers.Control, EditorMode.Normal, EditorIntent.ToggleSettings, "Settings"),
    ];

    /// <summary>
    /// The keys shared by crop and overlay placement.
    /// </summary>
    /// <remarks>
    /// Both rectangles are aspect-locked — a crop to the output ratio so zoom-to-fill
    /// never needs a pad, an overlay to its own source so the picture is never stretched —
    /// which collapses resizing from four directions to one grow/shrink pair.
    /// </remarks>
    private static KeyBinding[] PlacementBindings(EditorMode mode, string commitDescription) =>
    [
        new(EditorKey.Enter, EditorModifiers.None, mode, EditorIntent.Commit, commitDescription),
        new(EditorKey.Escape, EditorModifiers.None, mode, EditorIntent.Cancel, "Cancel"),
        new(EditorKey.Left, EditorModifiers.None, mode, EditorIntent.NudgeLeft, "Move left"),
        new(EditorKey.Right, EditorModifiers.None, mode, EditorIntent.NudgeRight, "Move right"),
        new(EditorKey.Up, EditorModifiers.None, mode, EditorIntent.NudgeUp, "Move up"),
        new(EditorKey.Down, EditorModifiers.None, mode, EditorIntent.NudgeDown, "Move down"),
        new(EditorKey.Up, EditorModifiers.Shift, mode, EditorIntent.Grow, "Bigger"),
        new(EditorKey.Down, EditorModifiers.Shift, mode, EditorIntent.Shrink, "Smaller"),
        new(EditorKey.D1, EditorModifiers.None, mode, EditorIntent.SnapTopLeft, "Snap to the top left"),
        new(EditorKey.D2, EditorModifiers.None, mode, EditorIntent.SnapTopRight, "Snap to the top right"),
        new(EditorKey.D3, EditorModifiers.None, mode, EditorIntent.SnapBottomLeft, "Snap to the bottom left"),
        new(EditorKey.D4, EditorModifiers.None, mode, EditorIntent.SnapBottomRight, "Snap to the bottom right"),
        new(EditorKey.D5, EditorModifiers.None, mode, EditorIntent.SnapCenter, "Centre"),
    ];

    /// <summary>Resolves a keystroke against the defaults.</summary>
    /// <remarks>
    /// The app resolves against <see cref="KeyBindings"/> instead, so that a user who has
    /// moved a key gets what they moved it to. This overload is the unconfigured answer.
    /// </remarks>
    public static EditorIntent Resolve(EditorKey key, EditorModifiers modifiers, EditorMode mode) =>
        KeyBindings.Default.Resolve(key, modifiers, mode);

    /// <summary>Default bindings grouped for the cheat sheet, in presentation order.</summary>
    public static IEnumerable<IGrouping<string, KeyBinding>> ForHelp() => KeyBindings.Default.ForHelp();

    /// <summary>
    /// Which section of the help sheet and the Controls page a binding belongs to.
    /// </summary>
    /// <remarks>
    /// Mode does the first cut: everything bound inside a placement mode is about moving a
    /// box around, whatever its intent. Only the Normal-mode bindings are sorted by what
    /// they do.
    /// </remarks>
    public static string Category(KeyBinding binding) => binding.Mode != EditorMode.Normal
        ? "Place a box"
        : binding.Intent switch
        {
            EditorIntent.PlayPause or EditorIntent.ShuttleForward or EditorIntent.ShuttleReverse
                or EditorIntent.Stop or EditorIntent.StepBack or EditorIntent.StepForward
                or EditorIntent.StepBackSecond or EditorIntent.StepForwardSecond
                or EditorIntent.PreviousEdit or EditorIntent.NextEdit
                or EditorIntent.GoToStart or EditorIntent.GoToEnd
                or EditorIntent.ToggleMute => "Navigate",

            EditorIntent.MarkIn or EditorIntent.MarkOut or EditorIntent.ClearMarks
                or EditorIntent.RippleDelete or EditorIntent.SplitAtPlayhead
                or EditorIntent.BeginCrop or EditorIntent.ClearCropAtPlayhead
                or EditorIntent.BeginOverlay or EditorIntent.RemoveOverlayAtPlayhead
                or EditorIntent.SyncOverlayAudio
                or EditorIntent.ToggleOverlayMute => "Edit",

            _ => "File and view",
        };
}
