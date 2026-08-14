using BertCut.Core.Edits;
using BertCut.Core.Input;
using BertCut.Core.Model;
using BertCut.Core.Session;
using BertCut.Core.Time;

namespace BertCut.Core.Tests;

public class SessionStoreTests
{
    private static Project Sample()
    {
        var p = TestProjects.TwoSources(baseFrames: 900, overlayFrames: 300);
        p = TimelineEdits.RippleDelete(p, new FrameRange(100, 250));
        p = TimelineEdits.SetCrop(p, new FrameRange(300, 500), TestProjects.HalfCrop());
        p = TimelineEdits.AddOverlay(p, new OverlayClip(
            new FrameRange(50, 200), SourceId: 2, SourceStartFrame: 30, Dest: new RectI(900, 500, 320, 192)));
        return p;
    }

    [Fact]
    public void A_project_survives_a_serialization_round_trip_unchanged()
    {
        var original = Sample();

        var restored = SessionStore.FromDocument(SessionStore.ToDocument(original));

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Timeline_positions_are_recomputed_rather_than_stored()
    {
        // Segment starts are the running total of the lengths, so the file cannot contain
        // a position that disagrees with the segment before it.
        var original = Sample();
        var document = SessionStore.ToDocument(original);

        Assert.All(document.Base, row => Assert.Equal(3, row.Length));

        var restored = SessionStore.FromDocument(document);
        Assert.Null(ProjectInvariants.Validate(restored));
    }

    [Fact]
    public void Frame_rates_round_trip_exactly_for_ntsc()
    {
        // A rate written as a float would come back as 29.97 and never match 30000/1001,
        // silently shifting every timestamp.
        var output = new OutputFormat(1920, 1080, Rational.Ntsc30);
        var source = TestProjects.Source(0, 500) with { FrameRate = Rational.Ntsc30 };
        var p = TimelineEdits.ImportSource(Project.Empty(output), source);

        var restored = SessionStore.FromDocument(SessionStore.ToDocument(p));

        Assert.Equal(Rational.Ntsc30, restored.Output.FrameRate);
        Assert.Equal(Rational.Ntsc30, restored.Sources[0].FrameRate);
    }

    [Fact]
    public void An_unknown_schema_version_is_rejected_rather_than_misread()
    {
        var document = SessionStore.ToDocument(Sample()) with { V = 99 };

        Assert.Throws<InvalidOperationException>(() => SessionStore.FromDocument(document));
    }

    [Fact]
    public void An_empty_project_round_trips()
    {
        var empty = Project.Empty(new OutputFormat(1280, 720, Rational.FromInt(30)));

        Assert.Equal(empty, SessionStore.FromDocument(SessionStore.ToDocument(empty)));
    }
}

public class KeyMapTests
{
    [Fact]
    public void The_core_editing_loop_is_reachable_without_modifiers()
    {
        // Mark in, mark out, ripple delete — three keystrokes, no chords. This is the
        // single most-repeated sequence in the app.
        Assert.Equal(EditorIntent.MarkIn, KeyMap.Resolve(EditorKey.I, EditorModifiers.None, EditorMode.Normal));
        Assert.Equal(EditorIntent.MarkOut, KeyMap.Resolve(EditorKey.O, EditorModifiers.None, EditorMode.Normal));
        Assert.Equal(EditorIntent.RippleDelete, KeyMap.Resolve(EditorKey.X, EditorModifiers.None, EditorMode.Normal));
    }

    [Fact]
    public void The_conventional_ripple_delete_chord_also_works()
    {
        Assert.Equal(
            EditorIntent.RippleDelete,
            KeyMap.Resolve(EditorKey.Delete, EditorModifiers.Shift, EditorMode.Normal));
    }

    [Fact]
    public void Shift_changes_what_a_key_means()
    {
        Assert.Equal(EditorIntent.BeginCrop, KeyMap.Resolve(EditorKey.C, EditorModifiers.None, EditorMode.Normal));
        Assert.Equal(EditorIntent.ClearCropAtPlayhead, KeyMap.Resolve(EditorKey.C, EditorModifiers.Shift, EditorMode.Normal));

        Assert.Equal(EditorIntent.StepForward, KeyMap.Resolve(EditorKey.Right, EditorModifiers.None, EditorMode.Normal));
        Assert.Equal(EditorIntent.StepForwardSecond, KeyMap.Resolve(EditorKey.Right, EditorModifiers.Shift, EditorMode.Normal));
    }

    [Fact]
    public void Arrow_keys_nudge_while_a_crop_is_being_positioned()
    {
        Assert.Equal(EditorIntent.NudgeLeft, KeyMap.Resolve(EditorKey.Left, EditorModifiers.None, EditorMode.Crop));
        Assert.Equal(EditorIntent.Commit, KeyMap.Resolve(EditorKey.Enter, EditorModifiers.None, EditorMode.Crop));
        Assert.Equal(EditorIntent.Cancel, KeyMap.Resolve(EditorKey.Escape, EditorModifiers.None, EditorMode.Crop));
    }

    [Fact]
    public void Undo_still_works_while_in_a_placement_mode()
    {
        // Being mid-crop must not trap the user without an escape from a prior mistake.
        Assert.Equal(EditorIntent.Undo, KeyMap.Resolve(EditorKey.Z, EditorModifiers.Control, EditorMode.Crop));
        Assert.Equal(EditorIntent.Undo, KeyMap.Resolve(EditorKey.Z, EditorModifiers.Control, EditorMode.Overlay));
    }

    [Fact]
    public void Both_redo_conventions_are_bound()
    {
        Assert.Equal(
            EditorIntent.Redo,
            KeyMap.Resolve(EditorKey.Z, EditorModifiers.Control | EditorModifiers.Shift, EditorMode.Normal));
        Assert.Equal(EditorIntent.Redo, KeyMap.Resolve(EditorKey.Y, EditorModifiers.Control, EditorMode.Normal));
    }

    /// <summary>
    /// The frame-step keys are the one place resolution is not a plain match.
    /// </summary>
    /// <remarks>
    /// <c>&lt;</c> and <c>&gt;</c> <i>are</i> shifted commas and periods, so on those two
    /// keys the modifier is how the character is typed rather than part of the gesture.
    /// Matching shift strictly would mean the keys the user was told to press do nothing.
    /// </remarks>
    [Theory]
    [InlineData(EditorModifiers.None)]
    [InlineData(EditorModifiers.Shift)]
    public void The_frame_step_keys_ignore_shift(EditorModifiers modifiers)
    {
        Assert.Equal(EditorIntent.StepBack, KeyMap.Resolve(EditorKey.Comma, modifiers, EditorMode.Normal));
        Assert.Equal(EditorIntent.StepForward, KeyMap.Resolve(EditorKey.Period, modifiers, EditorMode.Normal));
    }

    [Fact]
    public void Only_shift_is_forgiven_on_the_frame_step_keys()
    {
        // Dropping every modifier would let Ctrl+, resolve to a frame step and quietly
        // claim a chord a later feature may want.
        Assert.Equal(
            EditorIntent.None,
            KeyMap.Resolve(EditorKey.Comma, EditorModifiers.Control, EditorMode.Normal));
    }

    [Fact]
    public void The_frame_step_keys_are_inert_while_a_rectangle_is_being_placed()
    {
        // The same physical keys must not move the playhead out from under a crop box.
        Assert.Equal(EditorIntent.None, KeyMap.Resolve(EditorKey.Comma, EditorModifiers.None, EditorMode.Crop));
        Assert.Equal(EditorIntent.None, KeyMap.Resolve(EditorKey.Period, EditorModifiers.None, EditorMode.Overlay));
    }

    [Fact]
    public void No_two_bindings_collide()
    {
        var seen = new HashSet<(EditorKey, EditorModifiers, EditorMode)>();

        foreach (var binding in KeyMap.Bindings)
            Assert.True(
                seen.Add((binding.Key, binding.Modifiers, binding.Mode)),
                $"{binding.Key}+{binding.Modifiers} is bound twice in {binding.Mode} mode");
    }

    [Fact]
    public void Every_binding_carries_a_description_for_the_cheat_sheet()
    {
        Assert.All(KeyMap.Bindings, b => Assert.False(string.IsNullOrWhiteSpace(b.Description)));
        Assert.NotEmpty(KeyMap.ForHelp());
    }

    [Fact]
    public void Unbound_keys_resolve_to_nothing()
    {
        Assert.Equal(EditorIntent.None, KeyMap.Resolve(EditorKey.A, EditorModifiers.None, EditorMode.Normal));
        Assert.Equal(EditorIntent.None, KeyMap.Resolve(EditorKey.None, EditorModifiers.None, EditorMode.Normal));
    }
}
