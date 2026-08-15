using System.Windows;

namespace BertCut.App;

/// <summary>
/// What a toolbar button's tooltip says: the action's name, the key that does the same
/// thing, and a line about it when the icon alone is not the whole story.
/// </summary>
/// <remarks>
/// Built from the live key bindings every time they change, which is the point — a toolbar
/// that prints the shipped shortcut after the user has moved it is worse than one that
/// prints no shortcut at all.
/// </remarks>
public sealed record ShortcutTip(string Name, string Gesture = "", string Detail = "")
{
    /// <summary>Hidden rather than blank when the action has been unbound.</summary>
    public Visibility GestureVisibility => Gesture.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DetailVisibility => Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}
