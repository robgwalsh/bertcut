using System.Collections.Immutable;
using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Core.Tests;

/// <summary>Builders for the fixtures the edit and plan tests share.</summary>
internal static class TestProjects
{
    public static readonly OutputFormat Output1280 = new(1280, 768, Rational.FromInt(30));

    public static SourceMedia Source(int id, long frames, int width = 1280, int height = 768, int fps = 30) =>
        new(
            Id: id,
            Path: $@"C:\media\src{id}.mp4",
            ContentKey: $"key{id}",
            FrameCount: frames,
            Width: width,
            Height: height,
            FrameRate: Rational.FromInt(fps),
            IsVariableFrameRate: false,
            HasAudio: true,
            AudioSampleRate: 48000,
            VideoCodec: "h264",
            PixelFormat: "yuv420p");

    /// <summary>A project with one source laid end to end on the base track.</summary>
    public static Project Single(long frames = 1000)
    {
        var p = Project.Empty(Output1280);
        return TimelineEdits.ImportSource(p, Source(0, frames));
    }

    /// <summary>A project with two sources, only the first on the base track.</summary>
    public static Project TwoSources(long baseFrames = 1000, long overlayFrames = 400)
    {
        var p = TimelineEdits.ImportSource(Project.Empty(Output1280), Source(0, baseFrames));
        return TimelineEdits.ImportSource(p, Source(0, overlayFrames), appendToBase: false);
    }

    /// <summary>A crop rect matching the 1280x768 output aspect (5:3), scaled by /2.</summary>
    public static RectI HalfCrop(int x = 100, int y = 60) => new(x, y, 640, 384);

    public static Project WithBase(this Project p, params BaseSegment[] segments) =>
        p with { Base = [.. segments] };

    public static ImmutableArray<T> Arr<T>(params T[] items) => [.. items];
}
