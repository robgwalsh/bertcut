using BertCut.Core.Time;

namespace BertCut.Core.Model;

/// <summary>One entry on the undo stack.</summary>
public readonly record struct HistoryEntry(Project Project, string Label);

/// <summary>
/// Owns the current <see cref="Project"/> and its undo history.
/// </summary>
/// <remarks>
/// <para>
/// Undo is a stack of whole immutable snapshots rather than a command pattern. A project
/// is tens of small records — a heavily cut half-hour demo might reach a few hundred
/// segments, so 200 levels of history costs a couple of megabytes — while the heavy data
/// (per-frame timestamp tables, thumbnails, waveform peaks, decoded textures) is
/// deliberately held outside the document. Snapshotting is therefore nearly free, and
/// unlike a hand-written inverse for ripple delete it cannot be subtly wrong.
/// </para>
/// <para>
/// Only document state is undoable. Playhead, in/out marks, zoom, and selection are not:
/// having Ctrl+Z walk the playhead backwards is a well-known irritant in other editors.
/// </para>
/// <para>
/// Written only from the UI thread. Reader threads take <see cref="Current"/>, which is a
/// volatile read of an immutable object, so they never need a lock and can never observe a
/// half-applied edit.
/// </para>
/// </remarks>
public sealed class EditorDocument
{
    public const int MaxHistory = 200;

    private readonly List<HistoryEntry> _history = [];
    private Project _current;
    private int _index;
    private string? _openGestureId;

    public EditorDocument(Project initial, string label = "New project")
    {
        _current = initial;
        _history.Add(new HistoryEntry(initial, label));
        _index = 0;
    }

    /// <summary>The live document. Safe to read from any thread.</summary>
    public Project Current => Volatile.Read(ref _current);

    public bool CanUndo => _index > 0;

    public bool CanRedo => _index < _history.Count - 1;

    /// <summary>Label of the edit that Ctrl+Z would reverse.</summary>
    public string? UndoLabel => CanUndo ? _history[_index].Label : null;

    public string? RedoLabel => CanRedo ? _history[_index + 1].Label : null;

    /// <summary>Raised after the current project changes, on the calling (UI) thread.</summary>
    public event Action<Project>? Changed;

    /// <summary>
    /// Applies an edit and pushes the result onto the history.
    /// </summary>
    /// <param name="label">
    /// Human-readable description, shown in the "Undo: …" toast. Most of what "reliable
    /// undo" feels like to a user is being told what it just reversed.
    /// </param>
    /// <param name="gestureId">
    /// When supplied, consecutive applies sharing an id replace the top of the stack
    /// instead of pushing. Used so dragging an overlay rect leaves one undo step, not one
    /// per mouse-move.
    /// </param>
    public void Apply(string label, Func<Project, Project> edit, string? gestureId = null)
    {
        var next = edit(_current);
        if (ReferenceEquals(next, _current)) return;

#if DEBUG
        ProjectInvariants.Check(next);
#endif

        var coalescing = gestureId is not null && gestureId == _openGestureId && _index > 0;

        if (coalescing)
        {
            _history[_index] = new HistoryEntry(next, label);
        }
        else
        {
            // A new edit discards any redo tail.
            if (_index < _history.Count - 1)
                _history.RemoveRange(_index + 1, _history.Count - _index - 1);

            _history.Add(new HistoryEntry(next, label));
            _index = _history.Count - 1;

            if (_history.Count > MaxHistory)
            {
                var excess = _history.Count - MaxHistory;
                _history.RemoveRange(0, excess);
                _index -= excess;
            }
        }

        _openGestureId = gestureId;
        Publish(next);
    }

    /// <summary>Ends a coalescing gesture so the next edit starts a fresh undo step.</summary>
    public void EndGesture() => _openGestureId = null;

    public bool Undo()
    {
        if (!CanUndo) return false;
        _openGestureId = null;
        _index--;
        Publish(_history[_index].Project);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        _openGestureId = null;
        _index++;
        Publish(_history[_index].Project);
        return true;
    }

    /// <summary>
    /// Replaces the document wholesale, keeping it undoable.
    /// </summary>
    /// <remarks>
    /// Restoring an autosaved session goes through here, so Ctrl+Z discards the restore
    /// and leaves the user with a fresh timeline — which is why reopening a video needs no
    /// modal "restore or start over?" prompt on the fast path.
    /// </remarks>
    public void Replace(string label, Project project) => Apply(label, _ => project);

    /// <summary>A snapshot of history for autosave, oldest first, with the current index.</summary>
    public (IReadOnlyList<HistoryEntry> Entries, int Index) Snapshot() => (_history.ToArray(), _index);

    private void Publish(Project project)
    {
        Volatile.Write(ref _current, project);
        Changed?.Invoke(project);
    }
}
