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
/// FFmpeg deliberately does not come through here. It is an installed tool rather than
/// state, and <c>FfmpegRuntime</c> probes a fixed <c>LocalAppData\BertCut\ffmpeg</c> —
/// pointing that at a scratch directory would break discovery for anyone who installed it
/// there.
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
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BertCut");

    public static string Sessions => Path.Combine(Root, "sessions");

    public static string Controls => Path.Combine(Root, "controls.json");

    public static string Cache => Path.Combine(Root, "cache");
}
