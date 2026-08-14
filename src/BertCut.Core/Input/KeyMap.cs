namespace BertCut.Core.Input;

/// <summary>
/// Keys BertCut binds, independent of WPF.
/// </summary>
/// <remarks>
/// Core carries its own key enum rather than referencing <c>System.Windows.Input</c> so
/// the key map stays unit-testable and Core stays free of a UI framework dependency. The
/// app translates WPF keys into these at the edge.
/// </remarks>
public enum EditorKey
{
    None,
    Space, Left, Right, Up, Down, Home, End, Enter, Escape, Delete,
    A, C, E, I, J, K, L, M, O, P, S, V, X, Y, Z,
    D1, D2, D3, D4, D5,
    Minus, Equals, Backslash, Comma, Period,
    F1,
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
    BeginOverlay, RemoveOverlayAtPlayhead, ToggleOverlayMute,
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

    Undo, Redo,
    OpenFile, ImportSource, Export,

    /// <summary>Add a video to the end of the base track, as opposed to importing one to overlay.</summary>
    AppendSource,

    ZoomIn, ZoomOut, ZoomToFit,
    ToggleHelp,
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
/// There is no Ctrl+S. Autosave is the contract, and a Save key would imply it isn't.
/// </para>
/// </remarks>
public static class KeyMap
{
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
        new(EditorKey.C, EditorModifiers.None, EditorMode.Normal, EditorIntent.BeginCrop, "Crop the marked range"),
        new(EditorKey.C, EditorModifiers.Shift, EditorMode.Normal, EditorIntent.ClearCropAtPlayhead, "Clear the crop under the playhead"),
        new(EditorKey.P, EditorModifiers.None, EditorMode.Normal, EditorIntent.BeginOverlay, "Place an overlay over the marked range"),
        new(EditorKey.P, EditorModifiers.Shift, EditorMode.Normal, EditorIntent.RemoveOverlayAtPlayhead, "Remove the overlay under the playhead"),
        new(EditorKey.M, EditorModifiers.None, EditorMode.Normal, EditorIntent.ToggleOverlayMute, "Mute / unmute the overlay"),

        // Crop and overlay placement share their editing keys, so the gesture for putting
        // a rectangle somewhere is the same one in both modes.
        .. PlacementBindings(EditorMode.Crop, "Apply the crop"),
        .. PlacementBindings(EditorMode.Overlay, "Place the overlay"),

        // Only overlays have a source to slide against the base track.
        new(EditorKey.Left, EditorModifiers.Alt, EditorMode.Overlay, EditorIntent.TrimOverlayBack, "Overlay content back one frame"),
        new(EditorKey.Right, EditorModifiers.Alt, EditorMode.Overlay, EditorIntent.TrimOverlayForward, "Overlay content forward one frame"),

        // Document and view
        new(EditorKey.Z, EditorModifiers.Control, EditorMode.Normal, EditorIntent.Undo, "Undo"),
        new(EditorKey.Z, EditorModifiers.Control | EditorModifiers.Shift, EditorMode.Normal, EditorIntent.Redo, "Redo"),
        new(EditorKey.Y, EditorModifiers.Control, EditorMode.Normal, EditorIntent.Redo, "Redo"),
        new(EditorKey.O, EditorModifiers.Control, EditorMode.Normal, EditorIntent.OpenFile, "Open a video"),
        new(EditorKey.I, EditorModifiers.Control, EditorMode.Normal, EditorIntent.ImportSource, "Import another video to overlay"),
        new(EditorKey.A, EditorModifiers.Control, EditorMode.Normal, EditorIntent.AppendSource, "Add a video to the end of the timeline"),
        new(EditorKey.E, EditorModifiers.Control, EditorMode.Normal, EditorIntent.Export, "Export"),
        new(EditorKey.Equals, EditorModifiers.None, EditorMode.Normal, EditorIntent.ZoomIn, "Zoom in"),
        new(EditorKey.Minus, EditorModifiers.None, EditorMode.Normal, EditorIntent.ZoomOut, "Zoom out"),
        new(EditorKey.Backslash, EditorModifiers.None, EditorMode.Normal, EditorIntent.ZoomToFit, "Zoom to fit"),
        new(EditorKey.F1, EditorModifiers.None, EditorMode.Normal, EditorIntent.ToggleHelp, "Show / hide shortcuts"),
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

    /// <summary>
    /// Resolves a keystroke, preferring a binding for the current mode over the Normal one.
    /// </summary>
    public static EditorIntent Resolve(EditorKey key, EditorModifiers modifiers, EditorMode mode)
    {
        // < and > are typed as Shift + comma and Shift + period, so on these two keys the
        // shift is how you produce the character rather than part of the gesture. Both are
        // bound shift-agnostically, which means the frame-step keys answer to whichever of
        // the two the user thinks they are pressing.
        if (key is EditorKey.Comma or EditorKey.Period) modifiers &= ~EditorModifiers.Shift;

        foreach (var binding in Bindings)
            if (binding.Key == key && binding.Modifiers == modifiers && binding.Mode == mode)
                return binding.Intent;

        // Ctrl+Z and friends should still work while a crop is being positioned.
        if (mode != EditorMode.Normal)
            foreach (var binding in Bindings)
                if (binding.Key == key && binding.Modifiers == modifiers
                    && binding.Mode == EditorMode.Normal
                    && binding.Intent is EditorIntent.Undo or EditorIntent.Redo or EditorIntent.ToggleHelp)
                    return binding.Intent;

        return EditorIntent.None;
    }

    /// <summary>Bindings grouped for the cheat sheet, in presentation order.</summary>
    public static IEnumerable<IGrouping<string, KeyBinding>> ForHelp() =>
        Bindings
            .Where(b => b.Mode == EditorMode.Normal)
            .GroupBy(b => b.Intent switch
            {
                EditorIntent.PlayPause or EditorIntent.ShuttleForward or EditorIntent.ShuttleReverse
                    or EditorIntent.Stop or EditorIntent.StepBack or EditorIntent.StepForward
                    or EditorIntent.StepBackSecond or EditorIntent.StepForwardSecond
                    or EditorIntent.PreviousEdit or EditorIntent.NextEdit
                    or EditorIntent.GoToStart or EditorIntent.GoToEnd => "Navigate",

                EditorIntent.MarkIn or EditorIntent.MarkOut or EditorIntent.ClearMarks
                    or EditorIntent.RippleDelete or EditorIntent.SplitAtPlayhead
                    or EditorIntent.BeginCrop or EditorIntent.ClearCropAtPlayhead
                    or EditorIntent.BeginOverlay or EditorIntent.RemoveOverlayAtPlayhead
                    or EditorIntent.ToggleOverlayMute => "Edit",

                _ => "File and view",
            });
}
