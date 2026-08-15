using System.Text.Json;
using System.Text.Json.Serialization;
using BertCut.Core.Input;

namespace BertCut.Core.Session;

/// <summary>One customized binding, as it appears on disk.</summary>
public sealed record ControlDocument
{
    /// <summary>Which binding this is, by what it does and the key it shipped on.</summary>
    public string Slot { get; init; } = "";

    /// <summary>The key it now answers to, or <c>None</c> when the user unbound it.</summary>
    public string Key { get; init; } = "";

    public string Modifiers { get; init; } = "None";
}

/// <summary>Serialized form of the Controls page.</summary>
/// <remarks>
/// Only what was actually changed is written. That keeps the file readable, and it means a
/// later version that retunes a default gets to apply it to everyone who never touched that
/// key — rather than freezing the old default into every profile ever saved.
/// </remarks>
public sealed record ControlsDocument
{
    /// <summary>Schema version. The loader switches on this.</summary>
    public int V { get; init; } = 1;

    public List<ControlDocument> Bindings { get; init; } = [];
}

[JsonSerializable(typeof(ControlsDocument))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ControlsJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes the user's key bindings.
/// </summary>
/// <remarks>
/// One file for the machine, not one per video: which key crops is a property of the person
/// editing, not of the recording. Like the session store it never throws on the way in — a
/// controls file that cannot be read has to degrade into the shipped defaults, because the
/// alternative is an editor whose keyboard does nothing.
/// </remarks>
public static class KeyBindingStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BertCut", "controls.json");

    /// <summary>Loads the saved bindings, or the defaults when there are none.</summary>
    public static KeyBindings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return KeyBindings.Default;

            var document = JsonSerializer.Deserialize(
                File.ReadAllText(FilePath), ControlsJsonContext.Default.ControlsDocument);

            return document is null ? KeyBindings.Default : FromDocument(document);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            return KeyBindings.Default;
        }
    }

    /// <summary>
    /// Writes the bindings, atomically.
    /// </summary>
    /// <remarks>
    /// The Controls page saves on every change rather than behind an OK button, matching
    /// the rest of the app — so this runs often enough that a torn write has to be
    /// impossible rather than unlikely.
    /// </remarks>
    public static void Save(KeyBindings bindings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Nothing customized means nothing to remember: delete rather than leave an empty
        // file that looks like a profile.
        if (!bindings.IsCustomized)
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return;
        }

        var json = JsonSerializer.Serialize(
            ToDocument(bindings), ControlsJsonContext.Default.ControlsDocument);

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);

        if (File.Exists(FilePath)) File.Replace(temp, FilePath, null, ignoreMetadataErrors: true);
        else File.Move(temp, FilePath);
    }

    internal static ControlsDocument ToDocument(KeyBindings bindings) => new()
    {
        Bindings =
        [
            .. bindings.Customized.Select(slot => new ControlDocument
            {
                Slot = slot.Id,
                Key = slot.Key.ToString(),
                Modifiers = slot.Modifiers.ToString(),
            }),
        ],
    };

    internal static KeyBindings FromDocument(ControlsDocument document)
    {
        if (document.V != 1) throw new InvalidOperationException($"Unsupported controls version {document.V}.");

        return KeyBindings.From(Parse(document.Bindings));
    }

    /// <summary>
    /// Reads the rows a hand-edited file could contain, keeping only the ones that name a
    /// key this build has.
    /// </summary>
    private static IEnumerable<(string Slot, EditorKey Key, EditorModifiers Modifiers)> Parse(
        IEnumerable<ControlDocument> rows)
    {
        const EditorModifiers known = EditorModifiers.Shift | EditorModifiers.Control | EditorModifiers.Alt;

        foreach (var row in rows)
        {
            if (!Enum.TryParse<EditorKey>(row.Key, out var key) || !Enum.IsDefined(key)) continue;
            if (!Enum.TryParse<EditorModifiers>(row.Modifiers, out var modifiers)) continue;

            yield return (row.Slot, key, modifiers & known);
        }
    }
}
