using System.Collections.Immutable;

namespace BertCut.Core.Input;

/// <summary>One binding from <see cref="KeyMap"/>, and the gesture it currently answers to.</summary>
/// <remarks>
/// A slot is the unit of customization: the user does not add or remove actions, they move
/// the ones that exist onto different keys. Keeping the default alongside the current
/// gesture is what makes "reset this one" a local operation and what lets the file on disk
/// record only what was actually changed.
/// </remarks>
public sealed record BindingSlot(KeyBinding Default, EditorKey Key, EditorModifiers Modifiers)
{
    /// <summary>
    /// Stable identity for persistence.
    /// </summary>
    /// <remarks>
    /// Built from what the binding does and where it started, never from its position in
    /// the list — inserting a new default binding must not silently reassign somebody's
    /// customizations to the wrong actions.
    /// </remarks>
    public string Id => $"{Default.Mode}.{Default.Intent}.{Default.Key}.{(int)Default.Modifiers}";

    public EditorMode Mode => Default.Mode;

    public EditorIntent Intent => Default.Intent;

    public string Description => Default.Description;

    public bool IsCustom => Key != Default.Key || Modifiers != Default.Modifiers;

    /// <summary>True when the user has taken this action off the keyboard entirely.</summary>
    public bool IsUnbound => Key == EditorKey.None;

    public string Gesture => GestureText.Format(Key, Modifiers);

    public string DefaultGesture => GestureText.Format(Default.Key, Default.Modifiers);

    /// <summary>The binding as the dispatcher sees it.</summary>
    public KeyBinding Binding => Default with { Key = Key, Modifiers = Modifiers };
}

/// <summary>One row on the Controls page.</summary>
/// <remarks>
/// Crop and overlay placement deliberately share their editing keys, so "Move left" is two
/// slots — one per mode — that are one control as far as the user is concerned. An entry is
/// that control: rebinding it moves every slot underneath it together, which is the only
/// way the two modes can be guaranteed to stay in step.
/// </remarks>
public sealed record ControlEntry(string Category, string Label, ImmutableArray<BindingSlot> Slots)
{
    public BindingSlot Primary => Slots[0];

    public string Id => Primary.Id;

    public EditorIntent Intent => Primary.Intent;

    public EditorKey Key => Primary.Key;

    public EditorModifiers Modifiers => Primary.Modifiers;

    public string Gesture => Primary.Gesture;

    public string DefaultGesture => Primary.DefaultGesture;

    public bool IsCustom => Slots.Any(s => s.IsCustom);

    public bool IsUnbound => Primary.IsUnbound;
}

/// <summary>
/// A rebind, and whatever it took the key away from.
/// </summary>
/// <remarks>
/// A gesture answers to one action, so binding a key that is already spoken for has to
/// break something. It breaks the older binding rather than refusing the new one — refusing
/// leaves the user hunting for which of sixty rows is holding the key they want — and names
/// what it broke so the toolbar can say so.
/// </remarks>
public sealed record RebindResult(KeyBindings Bindings, ImmutableArray<string> Displaced);

/// <summary>
/// The key map the app actually dispatches against: <see cref="KeyMap"/> plus the user's
/// changes.
/// </summary>
/// <remarks>
/// Immutable, and cheap enough to rebuild on every keystroke of a rebind — sixty-odd
/// bindings — which keeps every customization a plain value swap rather than mutation
/// visible to a half-finished dispatch.
/// </remarks>
public sealed class KeyBindings
{
    /// <summary>The shipped bindings, with nothing customized.</summary>
    public static KeyBindings Default { get; } =
        new(ImmutableDictionary<string, (EditorKey, EditorModifiers)>.Empty);

    private readonly ImmutableDictionary<string, (EditorKey Key, EditorModifiers Modifiers)> _overrides;

    private KeyBindings(ImmutableDictionary<string, (EditorKey Key, EditorModifiers Modifiers)> overrides)
    {
        _overrides = overrides;

        Slots =
        [
            .. KeyMap.Bindings.Select(binding =>
            {
                var slot = new BindingSlot(binding, binding.Key, binding.Modifiers);
                return overrides.TryGetValue(slot.Id, out var custom)
                    ? slot with { Key = custom.Key, Modifiers = custom.Modifiers }
                    : slot;
            }),
        ];

        Effective = [.. Slots.Where(s => !s.IsUnbound).Select(s => s.Binding)];

        // Grouped by what the action is and where it started, so the two placement modes
        // collapse into one row while the two ways to ripple delete stay two.
        Entries =
        [
            .. Slots
                .GroupBy(s => (s.Intent, s.Default.Key, s.Default.Modifiers))
                .Select(group => new ControlEntry(
                    KeyMap.Category(group.First().Default),
                    string.Join(" · ", group.Select(s => s.Description).Distinct()),
                    [.. group])),
        ];
    }

    /// <summary>Every binding, default or moved, including the ones left unbound.</summary>
    public ImmutableArray<BindingSlot> Slots { get; }

    /// <summary>What the dispatcher matches against — unbound slots are simply absent.</summary>
    public ImmutableArray<KeyBinding> Effective { get; }

    /// <summary>The Controls page, in presentation order.</summary>
    public ImmutableArray<ControlEntry> Entries { get; }

    public bool IsCustomized => !_overrides.IsEmpty;

    /// <summary>The slots the user has moved or unbound, for persistence.</summary>
    public IEnumerable<BindingSlot> Customized => Slots.Where(s => s.IsCustom);

    /// <summary>
    /// Rebuilds a set from stored customizations.
    /// </summary>
    /// <remarks>
    /// Entries naming a slot that no longer exists, or naming the default gesture, are
    /// dropped rather than kept: a customization file written by an older version has to
    /// degrade into "these bindings are standard", never into a binding nothing can reach.
    /// </remarks>
    public static KeyBindings From(IEnumerable<(string Id, EditorKey Key, EditorModifiers Modifiers)> overrides)
    {
        var defaults = Default.Slots.ToDictionary(s => s.Id);
        var builder = ImmutableDictionary.CreateBuilder<string, (EditorKey, EditorModifiers)>();

        foreach (var (id, key, modifiers) in overrides)
        {
            if (!defaults.TryGetValue(id, out var slot)) continue;
            if (slot.Default.Key == key && slot.Default.Modifiers == modifiers) continue;

            builder[id] = (key, modifiers);
        }

        return new KeyBindings(builder.ToImmutable());
    }

    /// <summary>
    /// Resolves a keystroke, preferring a binding for the current mode over the Normal one.
    /// </summary>
    public EditorIntent Resolve(EditorKey key, EditorModifiers modifiers, EditorMode mode)
    {
        if (key == EditorKey.None) return EditorIntent.None;

        var intent = Match(key, modifiers, mode);

        // < and > are typed as Shift + comma and Shift + period, so on these two keys the
        // shift is how you produce the character rather than part of the gesture. Retrying
        // without it — rather than stripping it up front — means a chord the user has
        // deliberately bound to Shift+, still wins on the way past.
        if (intent == EditorIntent.None
            && key is EditorKey.Comma or EditorKey.Period
            && modifiers.HasFlag(EditorModifiers.Shift))
            intent = Match(key, modifiers & ~EditorModifiers.Shift, mode);

        if (intent != EditorIntent.None) return intent;

        // Ctrl+Z and friends should still work while a crop is being positioned.
        if (mode != EditorMode.Normal)
        {
            var global = Match(key, modifiers, EditorMode.Normal);
            if (global is EditorIntent.Undo or EditorIntent.Redo
                or EditorIntent.ToggleHelp or EditorIntent.ToggleSettings)
                return global;
        }

        return EditorIntent.None;
    }

    private EditorIntent Match(EditorKey key, EditorModifiers modifiers, EditorMode mode)
    {
        foreach (var binding in Effective)
            if (binding.Key == key && binding.Modifiers == modifiers && binding.Mode == mode)
                return binding.Intent;

        return EditorIntent.None;
    }

    /// <summary>The gesture to print against an action, or empty when it has none.</summary>
    public string GestureFor(EditorIntent intent, EditorMode mode = EditorMode.Normal)
    {
        foreach (var binding in Effective)
            if (binding.Intent == intent && binding.Mode == mode)
                return GestureText.Format(binding);

        return "";
    }

    /// <summary>Bindings grouped for the cheat sheet, in presentation order.</summary>
    public IEnumerable<IGrouping<string, KeyBinding>> ForHelp() =>
        Effective.Where(b => b.Mode == EditorMode.Normal).GroupBy(KeyMap.Category);

    /// <summary>The Controls page, grouped into its sections.</summary>
    public IEnumerable<IGrouping<string, ControlEntry>> ForSettings() => Entries.GroupBy(e => e.Category);

    /// <summary>
    /// Moves an action onto a gesture, taking that gesture off whatever else held it.
    /// </summary>
    /// <remarks>
    /// A collision only counts within the same mode, because that is the only place two
    /// bindings can both be candidates for one keystroke — <c>Enter</c> committing a crop
    /// has never been in the way of anything Normal mode does with it.
    /// </remarks>
    public RebindResult Rebind(ControlEntry entry, EditorKey key, EditorModifiers modifiers)
    {
        var moving = entry.Slots.Select(s => s.Id).ToHashSet();
        var modes = entry.Slots.Select(s => s.Mode).ToHashSet();

        var overrides = _overrides;
        var displaced = new List<string>();

        if (key != EditorKey.None)
            foreach (var slot in Slots)
            {
                if (moving.Contains(slot.Id) || slot.IsUnbound) continue;
                if (slot.Key != key || slot.Modifiers != modifiers || !modes.Contains(slot.Mode)) continue;

                overrides = overrides.SetItem(slot.Id, (EditorKey.None, EditorModifiers.None));
                displaced.Add(slot.Description);
            }

        foreach (var slot in entry.Slots)
            overrides = slot.Default.Key == key && slot.Default.Modifiers == modifiers
                ? overrides.Remove(slot.Id)
                : overrides.SetItem(slot.Id, (key, modifiers));

        return new RebindResult(new KeyBindings(overrides), [.. displaced.Distinct()]);
    }

    /// <summary>Takes an action off the keyboard without touching anything else.</summary>
    public KeyBindings Unbind(ControlEntry entry) =>
        Rebind(entry, EditorKey.None, EditorModifiers.None).Bindings;

    /// <summary>Puts one action back on its shipped gesture.</summary>
    /// <remarks>
    /// Restoring can collide just as rebinding can — the key it is going back to may have
    /// been given away in the meantime — so it goes through the same path.
    /// </remarks>
    public RebindResult Restore(ControlEntry entry) =>
        Rebind(entry, entry.Primary.Default.Key, entry.Primary.Default.Modifiers);

    /// <summary>Throws every customization away.</summary>
    public KeyBindings RestoreAll() => Default;
}
