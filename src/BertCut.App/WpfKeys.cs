using System.Windows.Input;
using BertCut.Core.Input;

namespace BertCut.App;

/// <summary>
/// The edge where WPF's keyboard becomes Core's.
/// </summary>
/// <remarks>
/// Core carries its own key enum so the key map stays unit-testable and free of a UI
/// framework; this is the one place that knows about both. Both the dispatcher and the
/// Controls page translate through here, so a key that can be pressed is a key that can be
/// bound.
/// </remarks>
internal static class WpfKeys
{
    /// <summary>
    /// The key a keystroke is really about.
    /// </summary>
    /// <remarks>
    /// Holding Alt turns every keystroke into <see cref="Key.System"/> and moves the real
    /// key to <see cref="KeyEventArgs.SystemKey"/>. Without this unwrap every Alt chord —
    /// including the overlay sync keys — arrives as a key nothing is bound to.
    /// </remarks>
    public static EditorKey Translate(KeyEventArgs e) =>
        Translate(e.Key == Key.System ? e.SystemKey : e.Key);

    /// <summary>True for the modifier keys themselves, which are never a binding on their own.</summary>
    public static bool IsModifier(KeyEventArgs e) =>
        (e.Key == Key.System ? e.SystemKey : e.Key) is
            Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

    public static EditorModifiers Modifiers()
    {
        var modifiers = EditorModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= EditorModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= EditorModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= EditorModifiers.Alt;
        return modifiers;
    }

    /// <summary>Maps WPF keys onto Core's framework-independent key enum.</summary>
    public static EditorKey Translate(Key key) => key switch
    {
        Key.Space => EditorKey.Space,
        Key.Left => EditorKey.Left,
        Key.Right => EditorKey.Right,
        Key.Up => EditorKey.Up,
        Key.Down => EditorKey.Down,
        Key.Home => EditorKey.Home,
        Key.End => EditorKey.End,
        Key.Enter => EditorKey.Enter,
        Key.Escape => EditorKey.Escape,
        Key.Delete => EditorKey.Delete,
        Key.Insert => EditorKey.Insert,
        Key.Tab => EditorKey.Tab,
        Key.PageUp => EditorKey.PageUp,
        Key.PageDown => EditorKey.PageDown,

        >= Key.A and <= Key.Z => EditorKey.A + (key - Key.A),
        >= Key.D0 and <= Key.D9 => EditorKey.D0 + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => EditorKey.D0 + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F12 => EditorKey.F1 + (key - Key.F1),

        Key.OemMinus or Key.Subtract => EditorKey.Minus,
        Key.OemPlus or Key.Add => EditorKey.Equals,
        Key.OemBackslash or Key.Oem5 => EditorKey.Backslash,
        Key.OemComma => EditorKey.Comma,
        Key.OemPeriod => EditorKey.Period,
        Key.Oem1 => EditorKey.Semicolon,
        Key.Oem7 => EditorKey.Quote,
        Key.Oem4 => EditorKey.LeftBracket,
        Key.Oem6 => EditorKey.RightBracket,
        Key.Oem2 => EditorKey.Slash,
        Key.Oem3 => EditorKey.Backtick,

        _ => EditorKey.None,
    };
}
