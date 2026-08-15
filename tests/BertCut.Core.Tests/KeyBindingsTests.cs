using BertCut.Core.Input;
using BertCut.Core.Session;

namespace BertCut.Core.Tests;

public class KeyBindingsTests
{
    private static ControlEntry Entry(KeyBindings bindings, EditorIntent intent) =>
        bindings.Entries.First(e => e.Intent == intent);

    [Fact]
    public void A_rebound_action_answers_to_the_new_key_and_not_the_old_one()
    {
        var bindings = KeyBindings.Default
            .Rebind(Entry(KeyBindings.Default, EditorIntent.BeginCrop), EditorKey.R, EditorModifiers.None)
            .Bindings;

        Assert.Equal(EditorIntent.BeginCrop, bindings.Resolve(EditorKey.R, EditorModifiers.None, EditorMode.Normal));
        Assert.Equal(EditorIntent.None, bindings.Resolve(EditorKey.C, EditorModifiers.None, EditorMode.Normal));

        // The defaults are a value, not a mutable global — customizing must not reach back
        // into what every other set is built from.
        Assert.Equal(EditorIntent.BeginCrop, KeyMap.Resolve(EditorKey.C, EditorModifiers.None, EditorMode.Normal));
    }

    [Fact]
    public void Taking_a_key_that_is_already_bound_unbinds_the_action_that_had_it()
    {
        // Two actions on one gesture would make dispatch depend on declaration order, which
        // is not a thing the user can see. The newer binding wins and the older is named.
        var result = KeyBindings.Default
            .Rebind(Entry(KeyBindings.Default, EditorIntent.SplitAtPlayhead), EditorKey.X, EditorModifiers.None);

        Assert.Equal(
            EditorIntent.SplitAtPlayhead,
            result.Bindings.Resolve(EditorKey.X, EditorModifiers.None, EditorMode.Normal));

        Assert.Contains("Ripple delete the marked range", result.Displaced);
        Assert.True(Entry(result.Bindings, EditorIntent.RippleDelete).IsUnbound);
    }

    [Fact]
    public void The_other_way_of_reaching_a_displaced_action_is_left_alone()
    {
        // Ripple delete is bound twice. Taking X away from it must not take Shift+Del too.
        var bindings = KeyBindings.Default
            .Rebind(Entry(KeyBindings.Default, EditorIntent.SplitAtPlayhead), EditorKey.X, EditorModifiers.None)
            .Bindings;

        Assert.Equal(
            EditorIntent.RippleDelete,
            bindings.Resolve(EditorKey.Delete, EditorModifiers.Shift, EditorMode.Normal));
    }

    [Fact]
    public void A_binding_in_another_mode_is_not_a_collision()
    {
        // Enter commits a placement and nothing in Normal mode wants it; binding Enter to a
        // Normal-mode action must not disturb the crop box.
        var bindings = KeyBindings.Default
            .Rebind(Entry(KeyBindings.Default, EditorIntent.MarkIn), EditorKey.Enter, EditorModifiers.None)
            .Bindings;

        Assert.Equal(EditorIntent.MarkIn, bindings.Resolve(EditorKey.Enter, EditorModifiers.None, EditorMode.Normal));
        Assert.Equal(EditorIntent.Commit, bindings.Resolve(EditorKey.Enter, EditorModifiers.None, EditorMode.Crop));
    }

    [Fact]
    public void Placement_keys_move_in_both_modes_at_once()
    {
        // Crop and overlay placement share their keys deliberately. One row on the Controls
        // page has to move both, or the gesture for putting a box somewhere stops being one
        // gesture.
        var entry = KeyBindings.Default.Entries.First(
            e => e.Intent == EditorIntent.NudgeLeft && e.Category == "Place a box");

        Assert.Equal(2, entry.Slots.Length);

        var bindings = KeyBindings.Default.Rebind(entry, EditorKey.H, EditorModifiers.None).Bindings;

        Assert.Equal(EditorIntent.NudgeLeft, bindings.Resolve(EditorKey.H, EditorModifiers.None, EditorMode.Crop));
        Assert.Equal(EditorIntent.NudgeLeft, bindings.Resolve(EditorKey.H, EditorModifiers.None, EditorMode.Overlay));
    }

    [Fact]
    public void An_unbound_action_cannot_be_reached_by_any_keystroke()
    {
        var bindings = KeyBindings.Default.Unbind(Entry(KeyBindings.Default, EditorIntent.ToggleMute));

        Assert.Equal(EditorIntent.None, bindings.Resolve(EditorKey.M, EditorModifiers.None, EditorMode.Normal));
        Assert.Equal(EditorIntent.None, bindings.Resolve(EditorKey.None, EditorModifiers.None, EditorMode.Normal));
        Assert.DoesNotContain(bindings.Effective, b => b.Intent == EditorIntent.ToggleMute);
    }

    /// <remarks>
    /// The sync key is bound in both Normal and Overlay mode, because it means the same
    /// thing whether the overlay has been committed or is still being positioned. Mode-scoped
    /// resolution is what makes that one action rather than two.
    /// </remarks>
    [Fact]
    public void Syncing_by_audio_is_reachable_while_placing_an_overlay_and_after()
    {
        Assert.Equal(
            EditorIntent.SyncOverlayAudio,
            KeyMap.Resolve(EditorKey.A, EditorModifiers.None, EditorMode.Normal));

        Assert.Equal(
            EditorIntent.SyncOverlayAudio,
            KeyMap.Resolve(EditorKey.A, EditorModifiers.None, EditorMode.Overlay));

        // Not while dragging a crop, which has no source to slide.
        Assert.Equal(
            EditorIntent.None,
            KeyMap.Resolve(EditorKey.A, EditorModifiers.None, EditorMode.Crop));
    }

    /// <remarks>
    /// M used to mute an overlay, which never affected anything. It now mutes the preview,
    /// and the old intent is left unbound rather than removed.
    /// </remarks>
    [Fact]
    public void M_mutes_the_preview_and_no_key_reaches_the_old_overlay_mute()
    {
        Assert.Equal(
            EditorIntent.ToggleMute,
            KeyMap.Resolve(EditorKey.M, EditorModifiers.None, EditorMode.Normal));

        Assert.DoesNotContain(KeyMap.Bindings, b => b.Intent == EditorIntent.ToggleOverlayMute);
    }

    [Fact]
    public void Restoring_one_binding_leaves_the_others_customized()
    {
        var moved = KeyBindings.Default
            .Rebind(Entry(KeyBindings.Default, EditorIntent.BeginCrop), EditorKey.R, EditorModifiers.None).Bindings
            .Rebind(Entry(KeyBindings.Default, EditorIntent.MarkIn), EditorKey.Q, EditorModifiers.None).Bindings;

        var restored = moved.Restore(Entry(moved, EditorIntent.BeginCrop)).Bindings;

        Assert.False(Entry(restored, EditorIntent.BeginCrop).IsCustom);
        Assert.True(Entry(restored, EditorIntent.MarkIn).IsCustom);
        Assert.Equal(EditorIntent.BeginCrop, restored.Resolve(EditorKey.C, EditorModifiers.None, EditorMode.Normal));
    }

    [Fact]
    public void Moving_a_binding_back_onto_its_default_stops_counting_as_a_customization()
    {
        var moved = KeyBindings.Default
            .Rebind(Entry(KeyBindings.Default, EditorIntent.BeginCrop), EditorKey.R, EditorModifiers.None).Bindings;

        var back = moved.Rebind(Entry(moved, EditorIntent.BeginCrop), EditorKey.C, EditorModifiers.None).Bindings;

        Assert.False(back.IsCustomized);
        Assert.Empty(back.Customized);
    }

    [Fact]
    public void Every_action_appears_exactly_once_on_the_controls_page()
    {
        var entries = KeyBindings.Default.Entries;

        Assert.Equal(KeyBindings.Default.Slots.Length, entries.Sum(e => e.Slots.Length));
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Label)));
        Assert.Equal(entries.Length, entries.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void The_help_sheet_shows_the_keys_the_user_chose()
    {
        var bindings = KeyBindings.Default
            .Rebind(Entry(KeyBindings.Default, EditorIntent.BeginCrop), EditorKey.R, EditorModifiers.None).Bindings;

        var gestures = bindings.ForHelp().SelectMany(g => g).Select(GestureText.Format).ToList();

        Assert.Contains("R", gestures);
        Assert.DoesNotContain("C", gestures);
        Assert.Equal("R", bindings.GestureFor(EditorIntent.BeginCrop));
    }

    [Fact]
    public void A_deliberate_shift_chord_on_the_comma_key_beats_the_shift_forgiveness()
    {
        // < is a shifted comma, so Shift is normally forgiven there. A user who binds
        // Shift+, to something must still get that something.
        var bindings = KeyBindings.Default
            .Rebind(Entry(KeyBindings.Default, EditorIntent.MarkOut), EditorKey.Comma, EditorModifiers.Shift)
            .Bindings;

        Assert.Equal(EditorIntent.MarkOut, bindings.Resolve(EditorKey.Comma, EditorModifiers.Shift, EditorMode.Normal));
        Assert.Equal(EditorIntent.StepBack, bindings.Resolve(EditorKey.Comma, EditorModifiers.None, EditorMode.Normal));
    }

    [Fact]
    public void Gestures_read_the_way_the_key_is_labelled()
    {
        Assert.Equal("Ctrl + Shift + Z", GestureText.Format(EditorKey.Z, EditorModifiers.Control | EditorModifiers.Shift));
        Assert.Equal("Shift + ↑", GestureText.Format(EditorKey.Up, EditorModifiers.Shift));
        Assert.Equal("<", GestureText.Format(EditorKey.Comma, EditorModifiers.None));
        Assert.Equal("5", GestureText.Format(EditorKey.D5, EditorModifiers.None));
        Assert.Equal("", GestureText.Format(EditorKey.None, EditorModifiers.Control));
    }
}

public class KeyBindingStoreTests
{
    private static ControlEntry Entry(KeyBindings bindings, EditorIntent intent) =>
        bindings.Entries.First(e => e.Intent == intent);

    private static KeyBindings Customized() =>
        KeyBindings.Default
            .Rebind(Entry(KeyBindings.Default, EditorIntent.BeginCrop), EditorKey.R, EditorModifiers.Control).Bindings;

    [Fact]
    public void Customizations_survive_a_round_trip()
    {
        var original = Customized();

        var restored = KeyBindingStore.FromDocument(KeyBindingStore.ToDocument(original));

        Assert.Equal(
            EditorIntent.BeginCrop,
            restored.Resolve(EditorKey.R, EditorModifiers.Control, EditorMode.Normal));
        Assert.Single(restored.Customized);
    }

    [Fact]
    public void Only_what_was_changed_is_written()
    {
        // A file that pinned all sixty bindings would freeze today's defaults into every
        // profile, and a retuned default would never reach anyone.
        var document = KeyBindingStore.ToDocument(Customized());

        Assert.Single(document.Bindings);
        Assert.Equal("R", document.Bindings[0].Key);
        Assert.Equal("Control", document.Bindings[0].Modifiers);
    }

    [Fact]
    public void An_unbound_action_round_trips_as_unbound()
    {
        var original = KeyBindings.Default.Unbind(Entry(KeyBindings.Default, EditorIntent.SplitAtPlayhead));

        var restored = KeyBindingStore.FromDocument(KeyBindingStore.ToDocument(original));

        Assert.True(Entry(restored, EditorIntent.SplitAtPlayhead).IsUnbound);
        Assert.Equal(EditorIntent.None, restored.Resolve(EditorKey.S, EditorModifiers.None, EditorMode.Normal));
    }

    [Fact]
    public void Rows_this_build_does_not_understand_are_dropped_rather_than_believed()
    {
        var document = new ControlsDocument
        {
            Bindings =
            [
                new() { Slot = "Normal.SomethingRemoved.Q.0", Key = "Q", Modifiers = "None" },
                new() { Slot = "Normal.BeginCrop.C.0", Key = "NotAKey", Modifiers = "None" },
                new() { Slot = "Normal.BeginCrop.C.0", Key = "R", Modifiers = "None" },
            ],
        };

        var restored = KeyBindingStore.FromDocument(document);

        Assert.Single(restored.Customized);
        Assert.Equal(EditorIntent.BeginCrop, restored.Resolve(EditorKey.R, EditorModifiers.None, EditorMode.Normal));
    }

    [Fact]
    public void An_unknown_schema_version_is_rejected_rather_than_misread()
    {
        var document = KeyBindingStore.ToDocument(Customized()) with { V = 99 };

        Assert.Throws<InvalidOperationException>(() => KeyBindingStore.FromDocument(document));
    }

    [Fact]
    public void Nothing_customized_round_trips_to_the_defaults()
    {
        var restored = KeyBindingStore.FromDocument(KeyBindingStore.ToDocument(KeyBindings.Default));

        Assert.False(restored.IsCustomized);
    }
}
