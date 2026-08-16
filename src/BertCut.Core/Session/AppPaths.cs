namespace BertCut.Core.Session;

/// <summary>
/// Where BertCut keeps its per-user state.
/// </summary>
/// <remarks>
/// <para>
/// One place decides this so that an automated run can move all of it at once. Sessions
/// restore by content key, so a test that opened a video and cut it would otherwise be
/// restored into the next run — and into the user's real editor the next time they opened
/// the same file. The override exists to make that impossible.
/// </para>
/// <para>
/// <strong>State lives in the profile, never in <c>%LOCALAPPDATA%\BertCut</c>.</strong> That is
/// where Velopack installs the app, and the installer <em>deletes that directory</em> on install
/// and on every update — so anything kept there survives exactly until the first update lands.
/// It used to be the state root, which is what <see cref="MigrateLegacyData"/> is for.
/// </para>
/// <para>
/// FFmpeg deliberately does not come through here. It is an installed tool rather than
/// state, and <c>FfmpegRuntime</c> probes fixed locations — pointing those at a scratch
/// directory would break discovery for anyone who installed it there.
/// </para>
/// </remarks>
public static class AppPaths
{
    /// <summary>Environment variable that relocates <see cref="Root"/>.</summary>
    public const string OverrideVariable = "BERTCUT_STATE_DIR";

    /// <summary>
    /// The state directory, honouring the override.
    /// </summary>
    /// <remarks>
    /// Read on every access rather than cached, so a harness can set the variable in-process
    /// before the first store call and still be obeyed.
    /// </remarks>
    public static string Root =>
        Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } dir
            ? dir
            : Default;

    public static string Sessions => Path.Combine(Root, "sessions");

    public static string Controls => Path.Combine(Root, "controls.json");

    public static string Cache => Path.Combine(Root, "cache");

    /// <summary>Where state lives when nothing has overridden it: <c>%USERPROFILE%\.bertcut</c>.</summary>
    private static string Default => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bertcut");

    /// <summary>Where state used to live, and where Velopack now installs the app.</summary>
    private static string Legacy => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BertCut");

    /// <summary>
    /// Moves state left behind by builds that kept it in <c>%LOCALAPPDATA%\BertCut</c>, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called before anything reads a session. It is a move rather than a copy, so a downgrade to
    /// a pre-Velopack build finds nothing — which is correct, because the next installer run would
    /// delete what it found there anyway.
    /// </para>
    /// <para>
    /// Every failure is swallowed. A first launch that throws because a stale lock is held on one
    /// cache file is a far worse outcome than a lost thumbnail envelope, and the caches regenerate
    /// on demand.
    /// </para>
    /// </remarks>
    public static void MigrateLegacyData()
    {
        // A harness run points Root at a scratch directory and must never touch the real profile,
        // in either direction.
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 })
            return;

        Migrate(Legacy, Default);
    }

    /// <summary>
    /// <see cref="MigrateLegacyData"/> against explicit roots, so it can be tested without the two
    /// real profile directories being involved.
    /// </summary>
    internal static void Migrate(string source, string destination)
    {
        if (Directory.Exists(destination) || !Directory.Exists(source))
            return;

        try
        {
            Directory.CreateDirectory(destination);

            foreach (var name in new[] { "sessions", "cache" })
                MoveDirectory(Path.Combine(source, name), Path.Combine(destination, name));

            MoveFile(Path.Combine(source, "controls.json"), Path.Combine(destination, "controls.json"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Next launch sees a destination that exists and stops trying, which is the right
            // answer: a half-moved state directory is still a usable one.
        }
    }

    private static void MoveDirectory(string from, string to)
    {
        if (!Directory.Exists(from) || Directory.Exists(to)) return;

        try
        {
            Directory.Move(from, to);
            return;
        }
        catch (IOException)
        {
            // Both roots are normally under the user's profile and so on one volume, but a
            // redirected LocalAppData is not, and Directory.Move refuses to cross volumes.
            // There is no second attempt after this launch — the destination exists from here
            // on — so the copy has to happen now or the sessions are gone.
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        CopyDirectory(from, to);

        try
        {
            Directory.Delete(from, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Copied is enough. A leftover original costs disk until the next installer run
            // clears the whole directory.
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from))
        {
            try
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // One unreadable file must not cost the rest of them.
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private static void MoveFile(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;

        try
        {
            File.Move(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The key map regenerates from its defaults; losing it costs custom bindings, and
            // failing the launch over it would cost everything.
        }
    }
}
