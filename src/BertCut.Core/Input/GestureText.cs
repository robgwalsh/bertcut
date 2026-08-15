namespace BertCut.Core.Input;

/// <summary>
/// Renders a keystroke the way the user thinks of it.
/// </summary>
/// <remarks>
/// This lives in Core rather than in the shell because three surfaces have to agree on it —
/// the help sheet, the Controls page, and every toolbar tooltip — and a keycap that says
/// something different from the key the dispatcher answers to is worse than no keycap.
/// </remarks>
public static class GestureText
{
    /// <summary>The gesture as a single string, or empty when the action is unbound.</summary>
    public static string Format(EditorKey key, EditorModifiers modifiers)
    {
        if (key == EditorKey.None) return "";

        var parts = new List<string>(4);

        // Ctrl, Shift, Alt regardless of the flag order, because that is the order every
        // Windows application prints them in.
        if (modifiers.HasFlag(EditorModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(EditorModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(EditorModifiers.Alt)) parts.Add("Alt");

        parts.Add(Name(key));

        return string.Join(" + ", parts);
    }

    public static string Format(KeyBinding binding) => Format(binding.Key, binding.Modifiers);

    /// <summary>The label printed on the keycap.</summary>
    public static string Name(EditorKey key) => key switch
    {
        EditorKey.None => "",

        EditorKey.Left => "←",
        EditorKey.Right => "→",
        EditorKey.Up => "↑",
        EditorKey.Down => "↓",
        EditorKey.PageUp => "PgUp",
        EditorKey.PageDown => "PgDn",
        EditorKey.Delete => "Del",
        EditorKey.Insert => "Ins",
        EditorKey.Escape => "Esc",

        EditorKey.Minus => "-",
        EditorKey.Equals => "=",
        EditorKey.Backslash => "\\",
        EditorKey.Semicolon => ";",
        EditorKey.Quote => "'",
        EditorKey.LeftBracket => "[",
        EditorKey.RightBracket => "]",
        EditorKey.Slash => "/",
        EditorKey.Backtick => "`",

        // Bound shift-agnostically, so the sheet shows the character people reach for
        // rather than the unshifted one they never think about.
        EditorKey.Comma => "<",
        EditorKey.Period => ">",

        >= EditorKey.D0 and <= EditorKey.D9 => ((char)('0' + (key - EditorKey.D0))).ToString(),

        _ => key.ToString(),
    };
}
