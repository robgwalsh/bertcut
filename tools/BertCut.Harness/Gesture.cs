using BertCut.Core.Input;

namespace BertCut.Harness;

/// <summary>
/// Reads a keystroke written the way a person would write it.
/// </summary>
/// <remarks>
/// The inverse of <see cref="GestureText"/>, and deliberately generous about it: a script
/// should be able to say <c>Ctrl+Z</c>, <c>ctrl + z</c>, <c>Del</c> or <c>Delete</c> and mean
/// the same key. It accepts every label the help sheet prints, including the arrows and the
/// <c>&lt;</c>/<c>&gt;</c> the frame-step keys are shown as, so a gesture can be copied
/// straight off a screenshot into a script.
/// </remarks>
internal static class Gesture
{
    public static (EditorKey Key, EditorModifiers Modifiers) Parse(string text)
    {
        var parts = text.Split(['+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new FormatException($"'{text}' is not a keystroke.");

        var modifiers = EditorModifiers.None;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => EditorModifiers.Control,
                "shift" => EditorModifiers.Shift,
                "alt" => EditorModifiers.Alt,
                _ => throw new FormatException($"'{parts[i]}' is not a modifier in '{text}'."),
            };
        }

        return (ParseKey(parts[^1], text), modifiers);
    }

    private static EditorKey ParseKey(string name, string whole) => name switch
    {
        "←" => EditorKey.Left,
        "→" => EditorKey.Right,
        "↑" => EditorKey.Up,
        "↓" => EditorKey.Down,
        "<" => EditorKey.Comma,
        ">" => EditorKey.Period,
        "-" => EditorKey.Minus,
        "=" => EditorKey.Equals,
        "\\" => EditorKey.Backslash,
        ";" => EditorKey.Semicolon,
        "'" => EditorKey.Quote,
        "[" => EditorKey.LeftBracket,
        "]" => EditorKey.RightBracket,
        "/" => EditorKey.Slash,
        "`" => EditorKey.Backtick,
        "," => EditorKey.Comma,
        "." => EditorKey.Period,
        ['0' or '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9'] =>
            EditorKey.D0 + (name[0] - '0'),

        _ => Named(name, whole),
    };

    private static EditorKey Named(string name, string whole)
    {
        var full = name.ToLowerInvariant() switch
        {
            "pgup" => "PageUp",
            "pgdn" => "PageDown",
            "del" => "Delete",
            "ins" => "Insert",
            "esc" => "Escape",
            "return" => "Enter",
            "spacebar" => "Space",
            _ => name,
        };

        if (Enum.TryParse<EditorKey>(full, ignoreCase: true, out var key) && key != EditorKey.None)
            return key;

        throw new FormatException($"'{name}' is not a key this editor knows, in '{whole}'.");
    }
}
