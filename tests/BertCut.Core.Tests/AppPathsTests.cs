using BertCut.Core.Session;

namespace BertCut.Core.Tests;

/// <summary>
/// The state root and the one-time move off it.
/// </summary>
/// <remarks>
/// The move matters more than it looks: <c>%LOCALAPPDATA%\BertCut</c> is now the Velopack install
/// directory, and the installer empties it. Anything the migration leaves behind is deleted by the
/// update that follows.
/// </remarks>
// BERTCUT_STATE_DIR is process-wide, and AudioPeaksTests and OverlaySyncTests set it too. Sharing
// their collection is what serialises the three of them; xUnit would otherwise run this class
// alongside those and have one clear the variable out from under another.
[Collection("ffmpeg")]
public class AppPathsTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "bertcut-apppaths-" + Guid.NewGuid().ToString("n"));

    private readonly string? _previousOverride =
        Environment.GetEnvironmentVariable(AppPaths.OverrideVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, _previousOverride);
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_override_relocates_every_path()
    {
        var root = Path.Combine(_temp, "state");
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, root);

        Assert.Equal(root, AppPaths.Root);
        Assert.Equal(Path.Combine(root, "sessions"), AppPaths.Sessions);
        Assert.Equal(Path.Combine(root, "controls.json"), AppPaths.Controls);
        Assert.Equal(Path.Combine(root, "cache"), AppPaths.Cache);
    }

    /// <summary>
    /// Read on every access rather than cached, so a harness that sets the variable in-process
    /// before its first store call is still obeyed.
    /// </summary>
    [Fact]
    public void The_override_is_read_every_time()
    {
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, Path.Combine(_temp, "one"));
        Assert.Equal(Path.Combine(_temp, "one"), AppPaths.Root);

        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, Path.Combine(_temp, "two"));
        Assert.Equal(Path.Combine(_temp, "two"), AppPaths.Root);
    }

    [Fact]
    public void Without_an_override_state_lives_in_the_profile()
    {
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, null);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bertcut");

        Assert.Equal(expected, AppPaths.Root);
    }

    /// <summary>
    /// The install directory is emptied by every update, so nothing may resolve into it.
    /// </summary>
    [Fact]
    public void The_default_root_is_not_the_install_directory()
    {
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, null);

        var install = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BertCut");

        Assert.False(
            AppPaths.Root.StartsWith(install, StringComparison.OrdinalIgnoreCase),
            $"{AppPaths.Root} is inside the Velopack install directory {install}.");
    }

    [Fact]
    public void Sessions_the_key_map_and_the_cache_all_move()
    {
        var legacy = Path.Combine(_temp, "legacy");
        var root = Path.Combine(_temp, "root");

        Directory.CreateDirectory(Path.Combine(legacy, "sessions"));
        Directory.CreateDirectory(Path.Combine(legacy, "cache", "peaks"));
        File.WriteAllText(Path.Combine(legacy, "sessions", "a.json"), "session");
        File.WriteAllText(Path.Combine(legacy, "cache", "peaks", "b.bin"), "peaks");
        File.WriteAllText(Path.Combine(legacy, "controls.json"), "keys");

        AppPaths.Migrate(legacy, root);

        Assert.Equal("session", File.ReadAllText(Path.Combine(root, "sessions", "a.json")));
        Assert.Equal("peaks", File.ReadAllText(Path.Combine(root, "cache", "peaks", "b.bin")));
        Assert.Equal("keys", File.ReadAllText(Path.Combine(root, "controls.json")));

        Assert.False(Directory.Exists(Path.Combine(legacy, "sessions")));
    }

    /// <summary>
    /// A destination that already exists is a copy that has already migrated — or one whose user
    /// has since edited a key binding. Overwriting either would be the migration undoing work.
    /// </summary>
    [Fact]
    public void An_existing_root_is_left_alone()
    {
        var legacy = Path.Combine(_temp, "legacy");
        var root = Path.Combine(_temp, "root");

        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(legacy, "controls.json"), "old");
        File.WriteAllText(Path.Combine(root, "controls.json"), "current");

        AppPaths.Migrate(legacy, root);

        Assert.Equal("current", File.ReadAllText(Path.Combine(root, "controls.json")));
    }

    /// <summary>A fresh install has nothing to move, and must not create an empty root for it.</summary>
    [Fact]
    public void Nothing_to_migrate_creates_nothing()
    {
        var root = Path.Combine(_temp, "root");

        AppPaths.Migrate(Path.Combine(_temp, "absent"), root);

        Assert.False(Directory.Exists(root));
    }

    /// <summary>
    /// The harness sets the override, and a scripted run must not reach into the real profile in
    /// either direction — reading a session out of it or moving one into it.
    /// </summary>
    [Fact]
    public void An_overridden_root_is_never_migrated_into()
    {
        var root = Path.Combine(_temp, "scratch");
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, root);

        AppPaths.MigrateLegacyData();

        Assert.False(Directory.Exists(root));
    }
}
