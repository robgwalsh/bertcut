using System.Security.Cryptography;
using System.Text;

namespace BertCut.Core.Session;

/// <summary>
/// The names two copies of BertCut find each other by, and the one line they exchange.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The names come from the state root, not from the user.</strong> Two copies are a
/// problem because they share a state directory — sessions restore by content key and autosave
/// every 750 ms, so two editors on one root write over each other. That makes the root the
/// thing to be single about. It also means a run pointed at a scratch root through
/// <c>BERTCUT_STATE_DIR</c> can never find, or be found by, the user's own editor.
/// </para>
/// <para>
/// The wire format is one UTF-8 line: the path to open, or an empty line for "just come
/// forward". Nothing else is ever sent, so there is nothing to version.
/// </para>
/// </remarks>
public static class SingleInstanceNames
{
    /// <summary>The mutex whose owner is the running copy.</summary>
    public static string Mutex(string stateRoot) => @"Local\BertCut." + Key(stateRoot);

    /// <summary>The pipe the running copy listens on. A bare name, without <c>\\.\pipe\</c>.</summary>
    public static string Pipe(string stateRoot) => "BertCut.instance." + Key(stateRoot);

    /// <summary>What a second launch sends: its path, or an empty line.</summary>
    public static string Format(LaunchArgs args) => (args.Path ?? "") + "\n";

    /// <summary>
    /// Reads what a second launch sent.
    /// </summary>
    /// <returns>
    /// False when the line is not one this app would have written — in which case nothing
    /// happens at all, not even coming forward. True with a null path for the empty line.
    /// </returns>
    public static bool TryParse(string? line, out string? path)
    {
        path = null;
        if (line is null) return false;

        var text = line.TrimEnd('\r', '\n');
        if (text.Length == 0) return true;

        if (!LaunchArgs.IsAcceptablePath(text)) return false;

        path = text;
        return true;
    }

    /// <summary>
    /// A short stable name for a directory, the same however it was spelled.
    /// </summary>
    private static string Key(string stateRoot)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateRoot)).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexString(hash, 0, 8);
    }
}
