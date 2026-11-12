namespace BertCut.Core.Picking;

/// <summary>
/// Finding BertBrowser, the file browser this app asks for a file when it is installed.
/// </summary>
/// <remarks>
/// <para>
/// A separate program by the same author, installed the same way (Velopack, per user), which
/// the user may or may not have. When it is there and new enough, every open and save here is
/// asked in it instead of in the Windows dialog; when it is not, nothing changes.
/// </para>
/// <para>
/// Pure: the environment, the registry and the disk are arguments, so the order of the search
/// and the version gate are tested rather than inferred from what this machine has installed.
/// </para>
/// </remarks>
public static class BertBrowserLocator
{
    public const string PackId = "BertBrowser";

    public const string ExeName = "BertBrowser.exe";

    /// <summary>The key, under the current user's hive, that the installer describes itself in.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + PackId;

    /// <summary>
    /// An explicit path to a BertBrowser to use, version unasked — for a copy built from source,
    /// which is installed nowhere and so has no version to read.
    /// </summary>
    public const string OverrideVariable = "BERTCUT_BERTBROWSER";

    /// <summary>
    /// Where a BertBrowser that takes <c>--pick</c> says it is: a key under the current user's hive
    /// whose <see cref="PickerValue"/> names the copy that last started.
    /// </summary>
    /// <remarks>
    /// Only a build that understands the option writes it, so its presence is the capability and
    /// no version is compared - which is what lets a copy built from source, installed nowhere,
    /// be found. The file is still checked: a build directory can be deleted without the registry
    /// hearing of it.
    /// </remarks>
    public const string PickerKey = @"Software\BertBrowser";

    public const string PickerValue = "PickerExe";

    /// <summary>
    /// The last release that does not understand <c>--pick</c>.
    /// </summary>
    /// <remarks>
    /// Asked of an older copy, the option is an unrecognised one: it opens an ordinary window,
    /// never answers, and the user waits out a timeout before the Windows dialog appears. So
    /// an older copy is treated as not installed.
    /// </remarks>
    public static readonly Version LastWithoutPicking = new(2, 0, 1);

    /// <summary>
    /// The BertBrowser to ask, or null when there is none that can be asked.
    /// </summary>
    /// <param name="overridePath">The value of <see cref="OverrideVariable"/>, if set.</param>
    /// <param name="localAppData"><c>%LOCALAPPDATA%</c>.</param>
    /// <param name="uninstallValue">Reads a value of <see cref="UninstallKey"/>; null when absent.</param>
    /// <param name="fileExists">Whether a path names a file.</param>
    /// <param name="registered">The <see cref="PickerValue"/> of <see cref="PickerKey"/>; null when absent.</param>
    public static string? Locate(
        string? overridePath,
        string? localAppData,
        Func<string, string?> uninstallValue,
        Func<string, bool> fileExists,
        string? registered = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.IsPathFullyQualified(overridePath) && fileExists(overridePath) ? overridePath : null;

        // The copy that last ran and said it can be asked. Before the installed one: it is the
        // one the user is using, and it needs no version to vouch for it. Gone, it is passed over.
        if (Root(registered) is { } said && fileExists(said)) return said;

        if (!UnderstandsPicking(uninstallValue("DisplayVersion"))) return null;

        // The default location first: it is where nearly every copy is. The installer's own
        // record second, because `winget install --location` can put the app elsewhere and
        // that value is the only thing that says where.
        //
        // `current\`, never the stub beside it: the stub applies a pending update and then
        // starts the app without passing on what it was asked.
        foreach (var root in new[] { Combine(localAppData, PackId), Root(uninstallValue("InstallLocation")) })
        {
            if (root is null) continue;

            var exe = Path.Combine(root, "current", ExeName);
            if (fileExists(exe)) return exe;
        }

        return null;
    }

    /// <summary>
    /// Whether a copy at this version takes <c>--pick</c>.
    /// </summary>
    /// <remarks>
    /// A version that cannot be read is a no: the cost of asking a copy that does not
    /// understand is a stray window and a wait, and the cost of not asking is the dialog the
    /// user has always had. A pre-release counts as the release it leads up to, so
    /// <c>2.0.2-unstable.7</c> is newer than <c>2.0.1</c>.
    /// </remarks>
    public static bool UnderstandsPicking(string? displayVersion)
    {
        if (string.IsNullOrWhiteSpace(displayVersion)) return false;

        var numeric = displayVersion.Trim().TrimStart('v', 'V');
        var end = numeric.IndexOfAny(['-', '+', ' ']);
        if (end >= 0) numeric = numeric[..end];

        return Version.TryParse(numeric, out var version) && version > LastWithoutPicking;
    }

    private static string? Combine(string? root, string name) =>
        Root(root) is { } full ? Path.Combine(full, name) : null;

    /// <summary>A usable directory, or null. A relative one would resolve against wherever this process started.</summary>
    private static string? Root(string? raw)
    {
        var trimmed = raw?.Trim().Trim('"');
        return !string.IsNullOrEmpty(trimmed) && Path.IsPathFullyQualified(trimmed) ? trimmed : null;
    }
}

/// <summary>
/// The command line that asks BertBrowser for a file.
/// </summary>
/// <remarks>
/// <c>--pick open|save --reply-pipe NAME [--pick-title T] [--pick-filter "*.a;*.b"]
/// [--pick-name FILE] [start]</c>. BertBrowser checks every field again on its side and refuses
/// the whole request over one it does not like, so anything doubtful is left out here rather
/// than sent: a picker without a title is better than no picker.
/// </remarks>
public static class BertBrowserArgs
{
    /// <summary>What BertBrowser will accept in a title, a filter or a name.</summary>
    private const int MaxText = 256;

    /// <summary>
    /// A pipe name BertBrowser will accept: letters, digits, <c>.</c>, <c>_</c>, <c>-</c>, at most 64.
    /// </summary>
    public static string PipeName(int processId, Guid unique) =>
        $"bertcut-pick-{processId}-{unique:N}"[..Math.Min(64, $"bertcut-pick-{processId}-{unique:N}".Length)];

    /// <summary>The arguments, one per element and unquoted — the process API does the quoting.</summary>
    public static IReadOnlyList<string> Build(PickRequest request, string pipeName)
    {
        var args = new List<string>
        {
            "--pick", request.Kind == PickKind.Save ? "save" : "open",
            "--reply-pipe", pipeName,
        };

        // Said as BertCut's, because the banner appears in another program and the user should
        // not have to work out who is asking.
        if (Plain("BertCut — " + request.Title) is { } title) args.AddRange(["--pick-title", title]);

        var patterns = string.Join(';', PickFilter.Patterns(request.Filter));
        if (patterns.Length > 0 && Plain(patterns) is { } filter) args.AddRange(["--pick-filter", filter]);

        if (request.Kind == PickKind.Save && IsFileName(request.DefaultName))
            args.AddRange(["--pick-name", request.DefaultName!]);

        if (request.StartDirectory is { Length: > 0 } start && Path.IsPathFullyQualified(start) && Plain(start) is not null)
        {
            // A trailing separator before the closing quote is how a path loses its last
            // character on the way through a Windows command line.
            args.Add(Path.TrimEndingDirectorySeparator(start));
        }

        return args;
    }

    /// <summary>Text with nothing in it a command line or a wire format could trip on, or null.</summary>
    private static string? Plain(string text)
    {
        var cleaned = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        if (cleaned.Length > MaxText) cleaned = cleaned[..MaxText];

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static bool IsFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxText
        && name == name.Trim()
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.EndsWith('.')
        && name is not ("." or "..");
}
