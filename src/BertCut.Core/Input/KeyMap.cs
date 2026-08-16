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

    /// <summary>
    /// Choosing what a new overlay will show, before there is anything to position.
    /// </summary>
    /// <remarks>
    /// A mode rather than a panel that quietly swallows keystrokes, so the card's keys resolve
    /// through the same map as every other key: they are rebindable, they appear on the
    /// Controls page, and a scripted run presses them exactly as a user does. It is
    /// deliberately not a <i>placement</i> mode — see <c>EditorViewModel.IsPlacing</c>, which
    /// asks for the two by name because there is no rectangle to move here yet.
    /// </remarks>
    OverlaySource,
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
    /// Remove whatever is picked out on the timeline: a base segment ripples away, an
    /// overlay comes off the top.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RemoveOverlayAtPlayhead"/>, which means one specific thing
    /// and goes on saying it. This one is the delete key: what it removes depends on what is
    /// selected, which is the only way one key can serve two kinds of clip.
    /// </remarks>
    DeleteSelection,

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

    /// <summary>
    /// What a new overlay will show. The three answers the source card offers.
    /// </summary>
    /// <remarks>
    /// Content only: none of them says where the clip goes, which is the playhead's job from
    /// the moment one of these is chosen. <see cref="ChooseOverlayFile"/> opens a file dialog
    /// and is therefore handled by the window rather than the view model, alongside
    /// <see cref="ImportSource"/>.
    /// </remarks>
    ChooseOverlayMarkedRange, ChooseOverlaySegment, ChooseOverlayFile,

    /// <summary>
    /// No longer bound: these slid a pending overlay's source in-point, which is the one thing
    /// positioning a clip must not change now that its content is chosen up front rather than
    /// inferred. A committed clip is still synced by <see cref="SyncOverlayAudio"/>, and still
    /// trimmed by its edges on the strip.
    /// </summary>
    /// <remarks>
    /// Kept in the enum for the same reason as <see cref="ToggleOverlayMute"/>: the values are
    /// named in stored key customizations, and a binding whose default has disappeared is
    /// dropped as an unknown id rather than crashing the Controls page.
    /// </remarks>
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
        new(EditorKey.Escape, EditorModifiers.None, EditorMode.Normal, EditorIntent.ClearMarks, "Clear in / out, deselect"),
        new(EditorKey.X, EditorModifiers.None, EditorMode.Normal, EditorIntent.RippleDelete, "Ripple delete the marked range"),
        new(EditorKey.Delete, EditorModifiers.Shift, EditorMode.Normal, EditorIntent.RippleDelete, "Ripple delete the marked range"),
        new(EditorKey.S, EditorModifiers.None, EditorMode.Normal, EditorIntent.SplitAtPlayhead, "Split at the playhead"),
        new(EditorKey.C, EditorModifiers.None, EditorMode.Normal, EditorIntent.BeginCrop, "Crop the marked range, or the whole video if nothing is marked"),
        new(EditorKey.C, EditorModifiers.Shift, EditorMode.Normal, EditorIntent.ClearCropAtPlayhead, "Clear the crop under the playhead"),
        new(EditorKey.P, EditorModifiers.None, EditorMode.Normal, EditorIntent.BeginOverlay, "Overlay a clip — asks what to overlay"),
        new(EditorKey.P, EditorModifiers.Shift, EditorMode.Normal, EditorIntent.RemoveOverlayAtPlayhead, "Remove the selected overlay, or the one under the playhead"),

        // The key everyone reaches for once something is picked out on the strip. Bare Delete
        // was free: ripple delete has always been on Shift+Delete, which is the gesture that
        // takes a marked range out of the timeline rather than a clip you have pointed at.
        new(EditorKey.Delete, EditorModifiers.None, EditorMode.Normal, EditorIntent.DeleteSelection, "Remove what is selected on the timeline"),
        new(EditorKey.A, EditorModifiers.None, EditorMode.Normal, EditorIntent.SyncOverlayAudio, "Sync the overlay to the base track by its sound"),
        new(EditorKey.M, EditorModifiers.None, EditorMode.Normal, EditorIntent.ToggleMute, "Mute / unmute the preview"),

        // Crop and overlay placement share their editing keys, so the gesture for putting
        // a rectangle somewhere is the same one in both modes.
        .. PlacementBindings(EditorMode.Crop, "Apply the crop"),
        .. PlacementBindings(EditorMode.Overlay, "Place the overlay"),

        // Saying what to overlay. Three rows on a card, and the digits printed on them.
        new(EditorKey.D1, EditorModifiers.None, EditorMode.OverlaySource, EditorIntent.ChooseOverlayMarkedRange, "Overlay the marked range"),
        new(EditorKey.D2, EditorModifiers.None, EditorMode.OverlaySource, EditorIntent.ChooseOverlaySegment, "Overlay the selected segment"),
        new(EditorKey.D3, EditorModifiers.None, EditorMode.OverlaySource, EditorIntent.ChooseOverlayFile, "Overlay a video file"),
        new(EditorKey.Escape, EditorModifiers.None, EditorMode.OverlaySource, EditorIntent.Cancel, "Cancel"),

        // Aiming a chosen clip. The arrows are spoken for by the rectangle, and these are the
        // keys that already mean "move the playhead" everywhere else in the editor — which is
        // exactly what they do here, the clip riding along with it.
        new(EditorKey.Comma, EditorModifiers.None, EditorMode.Overlay, EditorIntent.StepBack, "Move the overlay back one frame"),
        new(EditorKey.Period, EditorModifiers.None, EditorMode.Overlay, EditorIntent.StepForward, "Move the overlay forward one frame"),
        new(EditorKey.Comma, EditorModifiers.Shift, EditorMode.Overlay, EditorIntent.StepBackSecond, "Back one second"),
        new(EditorKey.Period, EditorModifiers.Shift, EditorMode.Overlay, EditorIntent.StepForwardSecond, "Forward one second"),
        new(EditorKey.Home, EditorModifiers.None, EditorMode.Overlay, EditorIntent.GoToStart, "Move it to the start"),
        new(EditorKey.End, EditorModifiers.None, EditorMode.Overlay, EditorIntent.GoToEnd, "Move it to the end"),
        new(EditorKey.A, EditorModifiers.None, EditorMode.Overlay, EditorIntent.SyncOverlayAudio, "Put it where the sound matches"),

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
    /// <remarks>
    /// Except the overlay source card, which is a mode without being a placement: its keys
    /// choose a clip rather than move anything, and listing them under a heading about boxes
    /// would file them where nobody looking for them would read.
    /// </remarks>
    public static string Category(KeyBinding binding) => binding.Mode switch
    {
        EditorMode.OverlaySource => "Overlay what",
        EditorMode.Crop or EditorMode.Overlay => "Place a box",
        _ => binding.Intent switch
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
                or EditorIntent.DeleteSelection or EditorIntent.SyncOverlayAudio
                or EditorIntent.ToggleOverlayMute => "Edit",

            _ => "File and view",
        },
    };
}
