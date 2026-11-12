namespace BertCut.Core.Updates;

/// <summary>
/// Which release channel a build came off, read from the build's own version string.
/// </summary>
/// <remarks>
/// <para>
/// An unstable build is packed by CI with <c>--channel unstable</c> and versioned
/// <c>1.2.3-unstable.42</c>, so the version already carries the answer — there is no second source
/// to drift from it, and unlike asking Velopack it answers for a build that was never installed.
/// </para>
/// <para>
/// The distinction is load-bearing rather than cosmetic: unstable ships as a GitHub
/// <em>pre-release</em> and stable does not, so it decides the <c>prerelease</c> flag the update
/// source is built with. Get it wrong in one direction and an unstable copy never finds its own
/// feed; get it wrong in the other and a stable copy is handed a pre-release that carries no feed
/// for its channel.
/// </para>
/// <para>
/// It matches the <em>first identifier of the SemVer pre-release tag</em>, not a substring, so
/// build metadata (<c>1.2.3+unstable</c>) and unrelated tags (<c>1.2.3-beta.1</c>) are both stable.
/// </para>
/// </remarks>
public static class ReleaseChannel
{
    /// <summary>The channel name CI passes to <c>vpk pack --channel</c> for builds off <c>main</c>.</summary>
    public const string Unstable = "unstable";

    /// <summary>
    /// The channel <c>install.ps1 -Local</c> packs a checkout's own build into. A copy installed from
    /// it never takes an update: the newest published release is not newer than what the developer
    /// is working on, and updates are mandatory.
    /// </summary>
    public const string Local = "local";

    /// <summary>Whether <paramref name="version"/> names a build from the unstable channel.</summary>
    public static bool IsUnstable(string? version) =>
        ChannelOf(version).Equals(Unstable, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="version"/> names a checkout's build installed locally.</summary>
    public static bool IsLocal(string? version) =>
        ChannelOf(version).Equals(Local, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first identifier of the pre-release tag, or empty when there is none.</summary>
    private static ReadOnlySpan<char> ChannelOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return default;

        var value = version.AsSpan().Trim();

        // Build metadata sits after '+' and is not part of the pre-release tag. Stripping it first
        // is what keeps "1.2.3+unstable-notes" a stable version.
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];

        var dash = value.IndexOf('-');
        if (dash < 0) return default;

        var prerelease = value[(dash + 1)..];

        // The tag is dot-separated identifiers and the channel is the first of them, so
        // "unstable.42" matches while "beta.1" does not.
        var dot = prerelease.IndexOf('.');
        return dot < 0 ? prerelease : prerelease[..dot];
    }
}
