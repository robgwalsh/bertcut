using BertCut.Core.Picking;
using BertCut.Core.Session;

namespace BertCut.Core.Tests;

/// <summary>
/// What a launch asks for, and what one copy of the app will accept from another.
/// </summary>
/// <remarks>
/// The hand-off pipe is reachable by anything running as the user, and what arrives on it goes
/// to a decoder. These hold the line that was written for that.
/// </remarks>
public class SingleInstanceTests
{
    [Fact]
    public void No_arguments_ask_for_nothing()
    {
        Assert.Null(LaunchArgs.Parse([], @"C:\work").Path);
        Assert.Null(LaunchArgs.Parse([""], @"C:\work").Path);
    }

    /// <summary>
    /// The copy that receives the request has its own working directory, so a relative path
    /// has to be settled by the copy that was given it.
    /// </summary>
    [Fact]
    public void A_relative_path_is_resolved_where_it_was_typed()
    {
        Assert.Equal(@"C:\work\clips\a.mp4", LaunchArgs.Parse([@"clips\a.mp4"], @"C:\work").Path);
        Assert.Equal(@"D:\b.mp4", LaunchArgs.Parse([@"D:\b.mp4"], @"C:\work").Path);
    }

    [Fact]
    public void Only_the_first_argument_is_read()
    {
        Assert.Equal(@"C:\a.mp4", LaunchArgs.Parse([@"C:\a.mp4", @"C:\b.mp4"], @"C:\work").Path);
    }

    [Theory]
    [InlineData(@"C:\clips\a.mp4", true)]
    [InlineData(@"\\server\share\a.mp4", true)]
    [InlineData(@"clips\a.mp4", false)]
    [InlineData(@"\\.\pipe\something", false)]
    [InlineData(@"\\?\C:\clips\a.mp4", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"C:\clips\*.mp4", false)]
    [InlineData("C:\\clips\\a\tb.mp4", false)]
    [InlineData("C:\\clips\\a.mp4\nC:\\other.mp4", false)]
    [InlineData("", false)]
    public void Only_a_plain_full_path_is_accepted(string path, bool accepted)
    {
        Assert.Equal(accepted, LaunchArgs.IsAcceptablePath(path));
    }

    [Fact]
    public void A_path_too_long_to_be_one_is_refused()
    {
        Assert.False(LaunchArgs.IsAcceptablePath(@"C:\" + new string('a', LaunchArgs.MaxPathLength)));
    }

    [Fact]
    public void A_request_survives_the_wire()
    {
        var line = SingleInstanceNames.Format(new LaunchArgs(@"C:\clips\a b.mp4"));

        Assert.True(SingleInstanceNames.TryParse(line, out var path));
        Assert.Equal(@"C:\clips\a b.mp4", path);
    }

    /// <summary>Launching the app a second time with no file still asks to see it.</summary>
    [Fact]
    public void An_empty_line_means_come_forward()
    {
        Assert.True(SingleInstanceNames.TryParse(SingleInstanceNames.Format(LaunchArgs.None), out var path));
        Assert.Null(path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("clips\\a.mp4\n")]
    [InlineData("\\\\.\\pipe\\x\n")]
    public void A_line_this_app_would_not_have_written_does_nothing(string? line)
    {
        Assert.False(SingleInstanceNames.TryParse(line, out var path));
        Assert.Null(path);
    }

    /// <summary>
    /// The names follow the state root, so a run on a scratch root cannot reach the user's
    /// own editor — and one root is one name however it was spelled.
    /// </summary>
    [Fact]
    public void The_names_follow_the_state_root()
    {
        Assert.Equal(SingleInstanceNames.Pipe(@"C:\Users\x\.bertcut"), SingleInstanceNames.Pipe(@"c:\users\X\.bertcut\"));
        Assert.NotEqual(SingleInstanceNames.Pipe(@"C:\Users\x\.bertcut"), SingleInstanceNames.Pipe(@"C:\Temp\scratch"));
        Assert.NotEqual(SingleInstanceNames.Mutex(@"C:\Users\x\.bertcut"), SingleInstanceNames.Mutex(@"C:\Temp\scratch"));

        Assert.StartsWith(@"Local\BertCut.", SingleInstanceNames.Mutex(@"C:\Temp\scratch"));
        Assert.DoesNotContain('\\', SingleInstanceNames.Pipe(@"C:\Temp\scratch"));
    }

    [Fact]
    public void A_filter_gives_up_its_wildcards()
    {
        Assert.Equal(
            ["*.mp4", "*.mkv"],
            PickFilter.Patterns("Video files|*.mp4;*.mkv|All files|*.*"));

        Assert.Equal(
            ["*.mp4", "*.gif"],
            PickFilter.Patterns("MP4 video|*.mp4|Animated GIF|*.gif"));

        Assert.Empty(PickFilter.Patterns("All files|*.*"));
    }
}
