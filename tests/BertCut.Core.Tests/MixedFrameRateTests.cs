using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Core.Tests;

/// <summary>
/// Appending a source that runs at a different rate from the project.
/// </summary>
/// <remarks>
/// A base segment's length is in <i>timeline</i> frames; the frames it reads are its source's,
/// counted at the source's own rate. <c>TimelineResolver</c> has always converted between the
/// two, and <c>TimelineEdits.ImportSource</c> has always written the converted length — but
/// <c>ProjectInvariants</c> compared the timeline length directly against the source's frame
/// count, so every mixed-rate append was rejected as reading past the end of its own source.
/// A 15 fps clip on a 30 fps timeline asks for twice as many timeline frames as it has frames,
/// which reads as corruption to a check that does not know the difference.
/// </remarks>
public class MixedFrameRateTests
{
    /// <summary>The case that failed: half the project's rate, so twice the timeline frames.</summary>
    [Fact]
    public void A_half_rate_source_appends()
    {
        var project = TimelineEdits.ImportSource(
            TestProjects.Single(frames: 120),
            TestProjects.Source(0, frames: 45, fps: 15));

        Assert.Null(ProjectInvariants.Validate(project));
        Assert.Equal(2, project.Base.Length);

        // 45 source frames at 15 fps is three seconds, which is 90 frames of a 30 fps timeline.
        Assert.Equal(90, project.Base[1].LengthFrames);
        Assert.Equal(210, project.DurationFrames);
    }

    /// <summary>And the other direction, where the source has more frames than the timeline needs.</summary>
    [Fact]
    public void A_double_rate_source_appends()
    {
        var project = TimelineEdits.ImportSource(
            TestProjects.Single(frames: 120),
            TestProjects.Source(0, frames: 120, fps: 60));

        Assert.Null(ProjectInvariants.Validate(project));
        Assert.Equal(60, project.Base[1].LengthFrames);
    }

    /// <summary>
    /// A GIF's rate is rarely a whole number, and never the project's.
    /// </summary>
    /// <remarks>
    /// 36 frames at 12.5 fps is 2.88 seconds — 86.4 frames of a 30 fps timeline, floored to 86 so
    /// the clip never claims a frame it does not have. This is the real shape of the fixture the
    /// harness builds, and the one that first exposed the invariant.
    /// </remarks>
    [Fact]
    public void A_fractional_rate_source_appends()
    {
        var gif = TestProjects.Source(0, frames: 36, width: 320, height: 240) with
        {
            FrameRate = new Rational(25, 2),
            HasAudio = false,
            VideoCodec = "gif",
        };

        var project = TimelineEdits.ImportSource(TestProjects.Single(frames: 120), gif);

        Assert.Null(ProjectInvariants.Validate(project));
        Assert.Equal(86, project.Base[1].LengthFrames);
    }

    /// <summary>
    /// The check still has to catch a segment that genuinely runs off the end of its source.
    /// </summary>
    [Fact]
    public void A_segment_reading_past_its_source_is_still_rejected()
    {
        var project = TestProjects.Single(frames: 120)
            .WithBase(new BaseSegment(0, 200, 1, 0));

        var error = ProjectInvariants.Validate(project);

        Assert.NotNull(error);
        Assert.Contains("past the source's", error);
    }

    /// <summary>Same rate is the overwhelmingly common case and must not have moved.</summary>
    [Fact]
    public void A_same_rate_source_is_unchanged()
    {
        var project = TimelineEdits.ImportSource(
            TestProjects.Single(frames: 120),
            TestProjects.Source(0, frames: 60));

        Assert.Null(ProjectInvariants.Validate(project));
        Assert.Equal(60, project.Base[1].LengthFrames);
        Assert.Equal(180, project.DurationFrames);
    }
}
