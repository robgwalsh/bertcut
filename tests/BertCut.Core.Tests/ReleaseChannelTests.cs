using BertCut.Core.Updates;

namespace BertCut.Core.Tests;

public class ReleaseChannelTests
{
    [Theory]
    [InlineData("1.2.3-unstable.42")]
    [InlineData("1.2.3-unstable")]
    [InlineData("1.2.3-UNSTABLE.1")]
    [InlineData("  1.2.3-unstable.7  ")]
    public void Recognises_a_build_off_the_unstable_channel(string version) =>
        Assert.True(ReleaseChannel.IsUnstable(version));

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Everything_else_is_a_release(string? version) =>
        Assert.False(ReleaseChannel.IsUnstable(version));

    /// <summary>
    /// Build metadata is not part of the pre-release tag, and a version carrying the word in the
    /// wrong half of it is a release. A substring match would get this backwards and hand a stable
    /// copy the unstable feed.
    /// </summary>
    [Theory]
    [InlineData("1.2.3+unstable")]
    [InlineData("1.2.3+unstable-notes")]
    public void Build_metadata_does_not_name_a_channel(string version) =>
        Assert.False(ReleaseChannel.IsUnstable(version));

    /// <summary>
    /// The channel is the first dot-separated identifier of the pre-release tag, so a tag that
    /// merely contains it later does not match.
    /// </summary>
    [Fact]
    public void Only_the_first_identifier_names_the_channel() =>
        Assert.False(ReleaseChannel.IsUnstable("1.2.3-beta.unstable.1"));

    /// <summary>
    /// The version CI stamps is the same string it hands <c>vpk pack --channel</c>, so the two
    /// halves of the arrangement have to agree on the spelling.
    /// </summary>
    [Fact]
    public void The_channel_name_is_what_CI_packs_with() =>
        Assert.Equal("unstable", ReleaseChannel.Unstable);
}
