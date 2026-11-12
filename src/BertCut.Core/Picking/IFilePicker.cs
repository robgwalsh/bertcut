namespace BertCut.Core.Picking;

/// <summary>Whether a pick names a file that exists or one about to be written.</summary>
public enum PickKind
{
    Open,
    Save,
}

/// <summary>
/// One question put to the user: which file.
/// </summary>
/// <param name="Kind">An existing file, or a destination.</param>
/// <param name="Title">What is being asked, in the app's own words.</param>
/// <param name="Filter">
/// A common-dialog filter — <c>Video files|*.mp4;*.mkv|All files|*.*</c>. It is the one format
/// every picker here can read: the Windows dialog takes it whole, and
/// <see cref="PickFilter.Patterns"/> pulls the wildcards out for one that cannot.
/// </param>
/// <param name="StartDirectory">Where to begin, or null for wherever the picker was last.</param>
/// <param name="DefaultName">The file name offered for a save.</param>
public sealed record PickRequest(
    PickKind Kind,
    string Title,
    string Filter,
    string? StartDirectory = null,
    string? DefaultName = null);

/// <summary>
/// Asks the user for a file.
/// </summary>
/// <remarks>
/// <para>
/// A seam because there is more than one way to ask. The Windows common dialog is always
/// there; BertBrowser is the better one when it is installed; and the harness answers from a
/// script, because a real picker belongs to the desktop and would appear on the user's screen
/// wherever the window under test is parked.
/// </para>
/// <para>
/// Asynchronous because a picker in another process takes as long as the user does, and the
/// window has to stay alive — and cancellable from its own side — for all of it.
/// </para>
/// </remarks>
public interface IFilePicker
{
    /// <returns>The full path chosen, or null when the user backed out.</returns>
    Task<string?> PickAsync(PickRequest request, CancellationToken cancellation = default);
}

/// <summary>Reads a common-dialog filter string.</summary>
public static class PickFilter
{
    /// <summary>
    /// The wildcards a filter names, without the catch-all.
    /// </summary>
    /// <remarks>
    /// Every group's patterns together, in order and once each: a save filter lists one type
    /// per group and all of them are acceptable. <c>*.*</c> and <c>*</c> are dropped because
    /// they say "anything", which is what an empty answer already means.
    /// </remarks>
    public static IReadOnlyList<string> Patterns(string filter)
    {
        var parts = filter.Split('|');
        var patterns = new List<string>();

        // Label, patterns, label, patterns: the odd fields are the ones that matter.
        for (var i = 1; i < parts.Length; i += 2)
        {
            foreach (var raw in parts[i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw is "*.*" or "*") continue;
                if (!patterns.Contains(raw, StringComparer.OrdinalIgnoreCase)) patterns.Add(raw);
            }
        }

        return patterns;
    }
}
