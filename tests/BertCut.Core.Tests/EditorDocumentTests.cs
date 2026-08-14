using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Core.Tests;

public class EditorDocumentTests
{
    private static EditorDocument NewDoc(long frames = 1000) => new(TestProjects.Single(frames));

    [Fact]
    public void Undo_restores_the_previous_project_exactly()
    {
        var doc = NewDoc();
        var original = doc.Current;

        doc.Apply("Ripple delete", p => TimelineEdits.RippleDelete(p, new FrameRange(100, 300)));
        Assert.Equal(800, doc.Current.DurationFrames);

        Assert.True(doc.Undo());
        Assert.Equal(original, doc.Current);
    }

    [Fact]
    public void Undo_and_redo_walk_the_history_in_both_directions()
    {
        var doc = NewDoc();

        doc.Apply("cut 1", p => TimelineEdits.RippleDelete(p, new FrameRange(0, 100)));
        doc.Apply("cut 2", p => TimelineEdits.RippleDelete(p, new FrameRange(0, 100)));
        Assert.Equal(800, doc.Current.DurationFrames);

        doc.Undo();
        Assert.Equal(900, doc.Current.DurationFrames);
        doc.Undo();
        Assert.Equal(1000, doc.Current.DurationFrames);

        Assert.False(doc.CanUndo);
        Assert.False(doc.Undo());

        doc.Redo();
        Assert.Equal(900, doc.Current.DurationFrames);
        doc.Redo();
        Assert.Equal(800, doc.Current.DurationFrames);
        Assert.False(doc.CanRedo);
    }

    [Fact]
    public void A_new_edit_discards_the_redo_tail()
    {
        var doc = NewDoc();

        doc.Apply("cut 1", p => TimelineEdits.RippleDelete(p, new FrameRange(0, 100)));
        doc.Apply("cut 2", p => TimelineEdits.RippleDelete(p, new FrameRange(0, 100)));
        doc.Undo();
        doc.Undo();

        doc.Apply("different cut", p => TimelineEdits.RippleDelete(p, new FrameRange(0, 500)));

        Assert.Equal(500, doc.Current.DurationFrames);
        Assert.False(doc.CanRedo);
    }

    [Fact]
    public void Repeated_cut_and_undo_round_trips_to_an_identical_project()
    {
        var doc = NewDoc();
        var original = doc.Current;

        // Ripple delete rewrites positions across the whole document, so a snapshot that
        // failed to capture some part of it would show up here.
        for (var i = 0; i < 25; i++)
        {
            doc.Apply("cut", p => TimelineEdits.RippleDelete(p, new FrameRange(10, 60)));
            doc.Undo();
        }

        Assert.Equal(original, doc.Current);
    }

    [Fact]
    public void An_edit_that_changes_nothing_does_not_push_history()
    {
        var doc = NewDoc();

        doc.Apply("no-op", p => TimelineEdits.RippleDelete(p, new FrameRange(50, 50)));

        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void A_gesture_collapses_into_a_single_undo_step()
    {
        var doc = new EditorDocument(TestProjects.TwoSources());
        doc.Apply("Add overlay", p => TimelineEdits.AddOverlay(p, new OverlayClip(
            new FrameRange(0, 100), 2, 0, new RectI(0, 0, 320, 192))));

        var beforeDrag = doc.Current;

        // Dragging an overlay fires an apply per mouse-move; the user expects one Ctrl+Z.
        for (var x = 0; x < 20; x++)
            doc.Apply("Move overlay", p => TimelineEdits.MoveOverlay(p, 0, new RectI(x, 0, 320, 192)), "drag-1");

        doc.EndGesture();

        Assert.Equal(19, doc.Current.Overlays[0].Dest.X);
        doc.Undo();
        Assert.Equal(beforeDrag, doc.Current);
    }

    [Fact]
    public void History_is_capped_and_drops_the_oldest_entries()
    {
        var doc = NewDoc(frames: 100_000);

        for (var i = 0; i < EditorDocument.MaxHistory + 50; i++)
            doc.Apply($"cut {i}", p => TimelineEdits.RippleDelete(p, new FrameRange(0, 1)));

        var (entries, index) = doc.Snapshot();

        Assert.Equal(EditorDocument.MaxHistory, entries.Count);
        Assert.Equal(entries.Count - 1, index);
        Assert.Equal(doc.Current, entries[index].Project);
    }

    [Fact]
    public void Undo_label_names_the_edit_that_would_be_reversed()
    {
        var doc = NewDoc();
        Assert.Null(doc.UndoLabel);

        doc.Apply("Ripple delete 00:00:04:12", p => TimelineEdits.RippleDelete(p, new FrameRange(0, 100)));

        Assert.Equal("Ripple delete 00:00:04:12", doc.UndoLabel);
    }

    [Fact]
    public void Restoring_a_session_is_itself_undoable()
    {
        // This is what lets a restored autosave need no modal prompt: Ctrl+Z discards it.
        var doc = NewDoc();
        var fresh = doc.Current;

        var restored = TimelineEdits.RippleDelete(fresh, new FrameRange(0, 400));
        doc.Replace("Restored session", restored);
        Assert.Equal(600, doc.Current.DurationFrames);

        doc.Undo();
        Assert.Equal(fresh, doc.Current);
    }
}
