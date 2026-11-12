namespace BertCut.Core.Session;

/// <summary>
/// What a launch asked for: at most, one file to open.
/// </summary>
/// <param name="Path">The file named on the command line, fully qualified, or null.</param>
/// <remarks>
/// Parsed here rather than in the application class so it can be tested, and because two
/// processes now read it: the copy that starts, and the copy already running that a second
/// launch hands its request to. The second has a different working directory, so a relative
/// path is resolved before it leaves the process that was given it.
/// </remarks>
public sealed record LaunchArgs(string? Path)
{
    /// <summary>Longer than any path Windows will open, and short enough to refuse a flood.</summary>
    public const int MaxPathLength = 4096;

    public static LaunchArgs None { get; } = new((string?)null);

    /// <summary>
    /// Reads a command line.
    /// </summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <param name="workingDirectory">What a relative path is relative to.</param>
    /// <remarks>
    /// Only the first argument is read, as it always was. One that cannot be a path — empty,
    /// or with characters no path holds — is nothing to open rather than an error: the app
    /// still starts, which is what the person who double-clicked it wanted.
    /// </remarks>
    public static LaunchArgs Parse(IReadOnlyList<string> args, string workingDirectory)
    {
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0])) return None;

        string full;

        try
        {
            full = System.IO.Path.GetFullPath(args[0], workingDirectory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return None;
        }

        return IsAcceptablePath(full) ? new LaunchArgs(full) : None;
    }

    /// <summary>
    /// Whether a path that arrived from another process may be opened.
    /// </summary>
    /// <remarks>
    /// The hand-off pipe is reachable by anything running as this user, and what arrives on it
    /// is about to be handed to a decoder. So: fully qualified, no control characters, no
    /// wildcards, and no device namespace — <c>\\.\</c> and <c>\\?\</c> name things that are
    /// not files. A UNC share is fine; recordings live on those.
    /// </remarks>
    public static bool IsAcceptablePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength) return false;
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return false;
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) return false;

        foreach (var c in path)
        {
            if (char.IsControl(c) || c is '*' or '?' or '"' or '<' or '>' or '|') return false;
        }

        return System.IO.Path.IsPathFullyQualified(path);
    }
}
