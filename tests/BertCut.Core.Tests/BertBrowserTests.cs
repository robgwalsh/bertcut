using BertCut.Core.Picking;

namespace BertCut.Core.Tests;

/// <summary>
/// Finding BertBrowser and asking it for a file.
/// </summary>
/// <remarks>
/// Both halves are a contract with another program. Where it is installed and which version
/// first understands the question decide whether it is asked at all; the arguments decide
/// whether it accepts — it refuses the whole request over one field it does not like.
/// </remarks>
public class BertBrowserTests
{
    private const string Local = @"C:\Users\u\AppData\Local";
    private const string Default = @"C:\Users\u\AppData\Local\BertBrowser\current\BertBrowser.exe";

    private static Func<string, string?> Registry(string? version, string? location = null) =>
        name => name switch
        {
            "DisplayVersion" => version,
            "InstallLocation" => location,
            _ => null,
        };

    private static Func<string, bool> Only(params string[] present) =>
        path => present.Contains(path, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_new_enough_copy_in_the_default_place_is_found()
    {
        Assert.Equal(Default, BertBrowserLocator.Locate(null, Local, Registry("2.1.0"), Only(Default)));
    }

    /// <summary>
    /// An older copy opens an ordinary window for a question it does not understand and never
    /// answers, so it is as good as absent.
    /// </summary>
    [Theory]
    [InlineData("2.0.1")]
    [InlineData("2.0.0")]
    [InlineData("1.9.9")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a version")]
    public void A_copy_too_old_to_be_asked_is_not_found(string? version)
    {
        Assert.Null(BertBrowserLocator.Locate(null, Local, Registry(version), Only(Default)));
    }

    [Theory]
    [InlineData("2.0.2", true)]
    [InlineData("2.0.2-unstable.7", true)]
    [InlineData("v2.1.0", true)]
    [InlineData("3.0.0+build5", true)]
    [InlineData("2.0.1-unstable.99", false)]
    [InlineData("2.0.1", false)]
    public void A_pre_release_counts_as_the_release_it_leads_to(string version, bool understands)
    {
        Assert.Equal(understands, BertBrowserLocator.UnderstandsPicking(version));
    }

    /// <summary><c>winget install --location</c> puts it elsewhere, and only the installer's record says where.</summary>
    [Fact]
    public void A_relocated_copy_is_found_through_its_install_location()
    {
        const string moved = @"D:\Apps\BertBrowser\current\BertBrowser.exe";

        Assert.Equal(
            moved,
            BertBrowserLocator.Locate(null, Local, Registry("2.1.0", @"D:\Apps\BertBrowser"), Only(moved)));
    }

    /// <summary>The stub beside <c>current</c> applies updates and drops what it was asked.</summary>
    [Fact]
    public void The_stub_beside_current_is_never_the_answer()
    {
        Assert.Null(BertBrowserLocator.Locate(
            null, Local, Registry("2.1.0"), Only(@"C:\Users\u\AppData\Local\BertBrowser\BertBrowser.exe")));
    }

    /// <summary>A copy built from source is installed nowhere, so it is named outright and not asked its version.</summary>
    [Fact]
    public void An_explicit_path_wins_and_skips_the_version()
    {
        const string built = @"C:\Source\bertbrowser\out\release\app\BertBrowser.exe";

        Assert.Equal(built, BertBrowserLocator.Locate(built, Local, Registry(null), Only(built, Default)));
        Assert.Null(BertBrowserLocator.Locate(built, Local, Registry("2.1.0"), Only(Default)));
        Assert.Null(BertBrowserLocator.Locate(@"relative\BertBrowser.exe", Local, Registry("2.1.0"), _ => true));
    }

    /// <summary>
    /// A BertBrowser that takes <c>--pick</c> says where it is each time it starts, which is how a
    /// copy that is installed nowhere is found with nothing set up.
    /// </summary>
    [Fact]
    public void The_copy_that_registered_itself_is_asked_with_no_version_and_before_an_installed_one()
    {
        const string built = @"C:\Source\bertbrowser\out\release\app\BertBrowser.exe";

        Assert.Equal(built, BertBrowserLocator.Locate(null, Local, Registry(null), Only(built), built));
        Assert.Equal(built, BertBrowserLocator.Locate(null, Local, Registry("2.1.0"), Only(built, Default), $"\"{built}\""));
    }

    /// <summary>A build directory is deleted without the registry hearing of it.</summary>
    [Fact]
    public void A_registered_copy_that_is_gone_or_not_a_full_path_is_passed_over()
    {
        const string gone = @"C:\Source\bertbrowser\out\release\app\BertBrowser.exe";

        Assert.Equal(Default, BertBrowserLocator.Locate(null, Local, Registry("2.1.0"), Only(Default), gone));
        Assert.Null(BertBrowserLocator.Locate(null, Local, Registry(null), Only(Default), gone));
        Assert.Null(BertBrowserLocator.Locate(null, Local, Registry(null), _ => true, @"relative\BertBrowser.exe"));
    }

    [Fact]
    public void Nothing_installed_is_null()
    {
        Assert.Null(BertBrowserLocator.Locate(null, Local, Registry("2.1.0"), Only()));
        Assert.Null(BertBrowserLocator.Locate(null, null, Registry("2.1.0"), _ => false));
    }

    [Fact]
    public void An_open_asks_with_its_title_filter_and_start()
    {
        var args = BertBrowserArgs.Build(
            new PickRequest(
                PickKind.Open,
                "Add a video",
                "Video files|*.mp4;*.mkv|All files|*.*",
                @"C:\my clips\"),
            "bertcut-pick-1-abcd");

        Assert.Equal(
            [
                "--pick", "open",
                "--reply-pipe", "bertcut-pick-1-abcd",
                "--pick-title", "BertCut \u2014 Add a video",
                "--pick-filter", "*.mp4;*.mkv",
                @"C:\my clips",
            ],
            args);
    }

    [Fact]
    public void A_save_offers_its_name()
    {
        var args = BertBrowserArgs.Build(
            new PickRequest(PickKind.Save, "Save as", "MP4 video|*.mp4|Animated GIF|*.gif", @"C:\clips", "clip-edit.mp4"),
            "p1");

        Assert.Equal(
            [
                "--pick", "save",
                "--reply-pipe", "p1",
                "--pick-title", "BertCut \u2014 Save as",
                "--pick-filter", "*.mp4;*.gif",
                "--pick-name", "clip-edit.mp4",
                @"C:\clips",
            ],
            args);
    }

    /// <summary>
    /// BertBrowser refuses a request over one bad field, so a doubtful one is left out: a
    /// picker with no name on offer is better than no picker.
    /// </summary>
    [Fact]
    public void A_field_that_would_be_refused_is_left_out()
    {
        var args = BertBrowserArgs.Build(
            new PickRequest(PickKind.Save, "Save\tas\n", "All files|*.*", @"relative\start", @"..\evil.mp4"),
            "p1");

        Assert.Equal(["--pick", "save", "--reply-pipe", "p1", "--pick-title", "BertCut \u2014 Saveas"], args);
    }

    [Fact]
    public void The_pipe_name_is_one_BertBrowser_accepts()
    {
        var name = BertBrowserArgs.PipeName(123456, Guid.Parse("0123456789abcdef0123456789abcdef"));

        Assert.StartsWith("bertcut-pick-123456-", name);
        Assert.True(name.Length <= 64);
        Assert.All(name, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));
    }
}