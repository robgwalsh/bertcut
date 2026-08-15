using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BertCut.Core.Input;

namespace BertCut.App;

/// <summary>
/// The settings screen: a list of pages on the left, one of them open on the right.
/// </summary>
/// <remarks>
/// <para>
/// Controls is the only page today. It is a list rather than a single pane because the
/// second page should be an entry in that list rather than a redesign of this one.
/// </para>
/// <para>
/// Nothing here is focusable, which is the same rule the editor behind it plays by: while
/// this screen is up the window hands it every keystroke, and a focused text box would take
/// the very keys the page exists to capture.
/// </para>
/// </remarks>
public partial class SettingsView : UserControl
{
    private readonly ObservableCollection<ControlSection> _sections = [];

    private KeyBindings _bindings = KeyBindings.Default;
    private ControlRow? _capturing;

    public SettingsView()
    {
        InitializeComponent();
        SectionList.ItemsSource = _sections;
    }

    /// <summary>Raised with the new bindings whenever the page changes one.</summary>
    public event Action<KeyBindings>? BindingsChanged;

    /// <summary>Raised when the page wants to be dismissed — Esc, the cross, or the settings key.</summary>
    public event Action? CloseRequested;

    /// <summary>Fills the page from the bindings in force.</summary>
    public void Open(KeyBindings bindings)
    {
        _bindings = bindings;

        Disarm();
        Build();
        Say(bindings.IsCustomized ? "" : "These are the keys BertCut ships with.");
    }

    /// <summary>Stands the page down, so it is never still waiting for a key next time.</summary>
    public void Close() => Disarm();

    /// <summary>
    /// Handles a keystroke while the screen is open, and reports that it did.
    /// </summary>
    /// <remarks>
    /// Always true: this page swallows the keyboard whole. A keystroke that fell through to
    /// the editor would edit the video behind a screen the user is reading, and while a row
    /// is armed every key — including the ones that normally cut — is data rather than a
    /// command.
    /// </remarks>
    public bool HandleKey(KeyEventArgs e)
    {
        // Ctrl on its own is the first half of Ctrl+E, not a binding. Acting on it would
        // make every chord impossible to type.
        if (WpfKeys.IsModifier(e)) return true;

        var key = WpfKeys.Translate(e);
        var modifiers = WpfKeys.Modifiers();

        if (_capturing is not { } row)
        {
            if (key == EditorKey.Escape
                || _bindings.Resolve(key, modifiers, EditorMode.Normal) == EditorIntent.ToggleSettings)
                CloseRequested?.Invoke();

            return true;
        }

        if (key == EditorKey.Escape)
        {
            Disarm();
            Say($"{row.Label} is still {row.Entry.Gesture}.");
            return true;
        }

        // Backspace is how you say "no key at all", so it is the one key that cannot be
        // bound — which is why it is deliberately missing from EditorKey.
        if (e.Key == Key.Back)
        {
            Unbind(row);
            return true;
        }

        if (key == EditorKey.None)
        {
            Say("BertCut does not have a name for that key — try another one.");
            return true;
        }

        Rebind(row, key, modifiers);
        return true;
    }

    // ---- the page ---------------------------------------------------------------------

    private void Build()
    {
        _sections.Clear();

        foreach (var section in _bindings.ForSettings())
            _sections.Add(new ControlSection(section.Key, [.. section.Select(e => new ControlRow(e))]));
    }

    /// <summary>
    /// Pushes changed bindings into the rows already on screen.
    /// </summary>
    /// <remarks>
    /// In place rather than rebuilt: a rebind can move a second row too — the one that lost
    /// the key — and replacing the list under a user who has scrolled to the bottom of it
    /// would throw their place away for a change they can see from where they are.
    /// </remarks>
    private void Refresh()
    {
        var entries = _bindings.Entries.ToDictionary(e => e.Id);

        foreach (var row in _sections.SelectMany(s => s.Rows))
            if (entries.TryGetValue(row.Id, out var entry))
                row.Update(entry);
    }

    private void Apply(KeyBindings bindings, string message)
    {
        _bindings = bindings;

        Refresh();
        Say(message);

        BindingsChanged?.Invoke(bindings);
    }

    private void Say(string message) => MessageLabel.Text = message;

    // ---- capturing a key ---------------------------------------------------------------

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ControlRow row }) return;

        // Clicking the armed row again is the way out for someone who reached for the mouse
        // rather than for Esc.
        if (ReferenceEquals(_capturing, row))
        {
            Disarm();
            Say($"{row.Label} is still {row.Entry.Gesture}.");
            return;
        }

        Disarm();

        _capturing = row;
        row.IsCapturing = true;

        Say($"Press the key for {row.Label} · Backspace for no key at all · Esc to leave it alone");
    }

    private void Disarm()
    {
        if (_capturing is null) return;

        _capturing.IsCapturing = false;
        _capturing = null;
    }

    private void Rebind(ControlRow row, EditorKey key, EditorModifiers modifiers)
    {
        var entry = row.Entry;

        if (entry.Key == key && entry.Modifiers == modifiers)
        {
            Disarm();
            Say($"{entry.Label} was already {entry.Gesture}.");
            return;
        }

        var result = _bindings.Rebind(entry, key, modifiers);

        Disarm();
        Apply(result.Bindings, $"{entry.Label} is now {GestureText.Format(key, modifiers)}." + Lost(result));
    }

    private void Unbind(ControlRow row)
    {
        var entry = row.Entry;

        Disarm();
        Apply(_bindings.Unbind(entry), $"{entry.Label} has no key now.");
    }

    private void OnRevertClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ControlRow row }) return;

        var entry = row.Entry;
        var result = _bindings.Restore(entry);

        Disarm();
        Apply(result.Bindings, $"{entry.Label} is back on {entry.DefaultGesture}." + Lost(result));
    }

    /// <summary>Names whatever the rebind had to take a key away from.</summary>
    private static string Lost(RebindResult result) => result.Displaced.Length switch
    {
        0 => "",
        1 => $" {result.Displaced[0]} has no key now.",
        _ => $" {string.Join(", ", result.Displaced)} have no key now.",
    };

    private void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        Disarm();

        if (!_bindings.IsCustomized)
        {
            Say("Nothing to put back — every key is already the one BertCut ships with.");
            return;
        }

        // The one control on this page with nothing behind it: there is no undo stack for
        // settings, so this is the place to ask rather than to apologise afterwards.
        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            "Reset every key?\n\nEvery change you have made on this page goes back to the "
            + "key BertCut ships with.",
            "BertCut",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        Apply(_bindings.RestoreAll(), "Every key is back to the way BertCut ships.");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}

/// <summary>A named group of controls, as one block on the page.</summary>
public sealed record ControlSection(string Name, IReadOnlyList<ControlRow> Rows);

/// <summary>
/// One control, as the page draws it.
/// </summary>
/// <remarks>
/// A thin, notifying wrapper over the Core entry rather than a copy of its fields: the key
/// map stays the single source of what a binding is, and this only decides how to say it.
/// </remarks>
public sealed class ControlRow(ControlEntry entry) : INotifyPropertyChanged
{
    private ControlEntry _entry = entry;
    private bool _capturing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ControlEntry Entry => _entry;

    public string Id => _entry.Id;

    public string Label => _entry.Label;

    /// <summary>What the keycap says: a gesture, an invitation, or the lack of a key.</summary>
    public string Gesture => _capturing
        ? "Press a key…"
        : _entry.IsUnbound ? "Not bound" : _entry.Gesture;

    /// <summary>Dims the cap — but not while it is armed, which has a colour of its own.</summary>
    public bool IsUnbound => !_capturing && _entry.IsUnbound;

    public bool IsCapturing
    {
        get => _capturing;
        set
        {
            if (_capturing == value) return;

            _capturing = value;
            Notify();
        }
    }

    /// <summary>Hidden rather than collapsed, so rows do not shift as keys are changed.</summary>
    public Visibility RevertVisibility => _entry.IsCustom ? Visibility.Visible : Visibility.Hidden;

    public ShortcutTip RevertTip => new("Back to the default", _entry.DefaultGesture);

    public void Update(ControlEntry entry)
    {
        _entry = entry;
        Notify();
    }

    /// <summary>Every property at once: they all derive from the one entry underneath.</summary>
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
