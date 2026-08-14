using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Core.Timeline;

namespace BertCut.Core.Export;

/// <summary>How an export will be produced.</summary>
public enum ExportMode
{
    /// <summary>Video stream-copied; only audio is re-encoded. Runs at roughly file-copy speed.</summary>
    LosslessVideo,

    /// <summary>Every kept segment re-encoded, because a crop or overlay changes the pixels.</summary>
    Render,
}

/// <summary>Why the lossless path was unavailable, for display in the export dialog.</summary>
public enum LosslessBlocker
{
    None,
    HasCrop,
    HasOverlay,
    MultipleSources,
    CutsMissKeyframes,
    Disabled,
}

/// <summary>One ffmpeg process to run.</summary>
public sealed record FfmpegStep(
    string Description,
    IReadOnlyList<string> Arguments,
    double DurationSeconds,
    string? WritesFile = null);

/// <summary>The complete sequence of work for one export.</summary>
public sealed record ExportPlan(
    ExportMode Mode,
    LosslessBlocker Blocker,
    IReadOnlyList<FfmpegStep> Steps,
    IReadOnlyList<string> TempFiles,
    IReadOnlyList<long> KeyframeSnappedBoundaries)
{
    public double TotalSeconds => Steps.Sum(s => s.DurationSeconds);
}

/// <summary>
/// Turns a <see cref="Project"/> into the ffmpeg invocations that produce its output file.
/// </summary>
/// <remarks>
/// Pure and headlessly testable: it takes the source index lookup and the encoder
/// capabilities as parameters rather than probing for them, so its output can be pinned by
/// golden tests without a GPU or an ffmpeg install.
/// </remarks>
public static class ExportPlanner
{
    public static ExportPlan Plan(
        Project project,
        ExportSettings settings,
        EncoderCapabilities capabilities,
        Func<int, SourceIndex> indexOf,
        string tempDirectory)
    {
        if (project.Base.IsEmpty)
            throw new InvalidOperationException("Nothing to export: the timeline is empty.");

        var plan = RenderPlan.Build(project);
        var blocker = FindLosslessBlocker(project, plan, settings, indexOf);

        return blocker == LosslessBlocker.None
            ? PlanLossless(project, plan, settings, indexOf, tempDirectory)
            : PlanRender(project, plan, settings, capabilities, indexOf, tempDirectory, blocker);
    }

    /// <summary>
    /// Determines whether video can be stream-copied, and if not, why.
    /// </summary>
    /// <remarks>
    /// The keyframe requirement is the subtle one. A copy cut cannot drop the frames
    /// between the preceding keyframe and the requested in-point, so unless every boundary
    /// already sits on a keyframe the output would gain up to a GOP of unwanted footage.
    /// The check is O(1) per boundary thanks to <see cref="SourceIndex.IsKeyFrame"/>.
    /// </remarks>
    public static LosslessBlocker FindLosslessBlocker(
        Project project,
        IReadOnlyList<FlatSegment> plan,
        ExportSettings settings,
        Func<int, SourceIndex> indexOf)
    {
        if (!settings.AllowLosslessFastPath) return LosslessBlocker.Disabled;
        if (!project.Crops.IsEmpty) return LosslessBlocker.HasCrop;
        if (!project.Overlays.IsEmpty) return LosslessBlocker.HasOverlay;

        var sourceIds = plan.Select(s => s.SourceId).Distinct().ToArray();
        if (sourceIds.Length != 1) return LosslessBlocker.MultipleSources;

        var index = indexOf(sourceIds[0]);
        foreach (var segment in plan)
            if (!index.IsKeyFrame(segment.SourceStartFrame))
                return LosslessBlocker.CutsMissKeyframes;

        return LosslessBlocker.None;
    }

    /// <summary>
    /// Reports where each cut boundary would land if snapped to a keyframe, so the UI can
    /// draw ghost marks before the user commits to the fast path.
    /// </summary>
    public static IReadOnlyList<long> SnappedBoundaries(
        IReadOnlyList<FlatSegment> plan,
        Func<int, SourceIndex> indexOf)
    {
        var result = new List<long>(plan.Count);
        foreach (var segment in plan)
        {
            var index = indexOf(segment.SourceId);
            result.Add(index.KeyFrameAtOrBefore(segment.SourceStartFrame));
        }

        return result;
    }

    private static ExportPlan PlanLossless(
        Project project,
        IReadOnlyList<FlatSegment> plan,
        ExportSettings settings,
        Func<int, SourceIndex> indexOf,
        string temp)
    {
        var steps = new List<FfmpegStep>();
        var tempFiles = new List<string>();
        var segmentFiles = new List<string>();

        for (var i = 0; i < plan.Count; i++)
        {
            var segment = plan[i];
            var source = project.RequireSource(segment.SourceId);
            var index = indexOf(segment.SourceId);
            var (start, end) = SourceSeconds(segment, index);

            var path = Path.Combine(temp, $"seg{i:D4}.mp4");
            segmentFiles.Add(path);
            tempFiles.Add(path);

            steps.Add(new FfmpegStep(
                $"Copying segment {i + 1} of {plan.Count}",
                FfmpegArgs.CopySegment(source.Path, start, end, path),
                end - start,
                path));
        }

        var listFile = Path.Combine(temp, "segments.txt");
        var videoFile = Path.Combine(temp, "video.mp4");
        tempFiles.Add(listFile);
        tempFiles.Add(videoFile);

        var totalSeconds = steps.Sum(s => s.DurationSeconds);

        steps.Add(new FfmpegStep(
            "Joining segments",
            FfmpegArgs.ConcatSegments(listFile, videoFile),
            totalSeconds,
            videoFile));

        AppendAudioAndMux(project, plan, settings, indexOf, temp, steps, tempFiles, videoFile, totalSeconds);

        return new ExportPlan(
            ExportMode.LosslessVideo,
            LosslessBlocker.None,
            steps,
            tempFiles,
            SnappedBoundaries(plan, indexOf));
    }

    private static ExportPlan PlanRender(
        Project project,
        IReadOnlyList<FlatSegment> plan,
        ExportSettings settings,
        EncoderCapabilities capabilities,
        Func<int, SourceIndex> indexOf,
        string temp,
        LosslessBlocker blocker)
    {
        var encoder = capabilities.SelectVideoEncoder();
        var steps = new List<FfmpegStep>();
        var tempFiles = new List<string>();
        var segmentFiles = new List<string>();

        for (var i = 0; i < plan.Count; i++)
        {
            var segment = plan[i];
            var source = project.RequireSource(segment.SourceId);
            var index = indexOf(segment.SourceId);
            var (start, end) = SourceSeconds(segment, index);

            string? overlayPath = null;
            double overlayStart = 0;
            if (segment.Overlay is { } overlay)
            {
                var overlaySource = project.RequireSource(overlay.SourceId);
                overlayPath = overlaySource.Path;
                overlayStart = indexOf(overlay.SourceId).SecondsOf(segment.OverlaySourceStartFrame);
            }

            var path = Path.Combine(temp, $"seg{i:D4}.mp4");
            segmentFiles.Add(path);
            tempFiles.Add(path);

            steps.Add(new FfmpegStep(
                $"Rendering segment {i + 1} of {plan.Count}",
                FfmpegArgs.RenderSegment(
                    segment,
                    new SegmentTiming(start, end, overlayStart),
                    project.Output,
                    source.Path,
                    overlayPath,
                    encoder,
                    settings.Quality,
                    capabilities.HasCudaHwaccel,
                    path),
                end - start,
                path));
        }

        var listFile = Path.Combine(temp, "segments.txt");
        var videoFile = Path.Combine(temp, "video.mp4");
        tempFiles.Add(listFile);
        tempFiles.Add(videoFile);

        var totalSeconds = steps.Sum(s => s.DurationSeconds);

        steps.Add(new FfmpegStep(
            "Joining segments",
            FfmpegArgs.ConcatSegments(listFile, videoFile),
            totalSeconds,
            videoFile));

        AppendAudioAndMux(project, plan, settings, indexOf, temp, steps, tempFiles, videoFile, totalSeconds);

        return new ExportPlan(ExportMode.Render, blocker, steps, tempFiles, SnappedBoundaries(plan, indexOf));
    }

    private static void AppendAudioAndMux(
        Project project,
        IReadOnlyList<FlatSegment> plan,
        ExportSettings settings,
        Func<int, SourceIndex> indexOf,
        string temp,
        List<FfmpegStep> steps,
        List<string> tempFiles,
        string videoFile,
        double totalSeconds)
    {
        var audioSpans = BuildAudioSpans(project, plan, indexOf, out var audioInputs);

        if (audioSpans.Count == 0)
        {
            steps.Add(new FfmpegStep(
                "Finalizing",
                FfmpegArgs.Finalize(videoFile, settings.OutputPath),
                Math.Max(1, totalSeconds * 0.05)));
            return;
        }

        var audioFile = Path.Combine(temp, "audio.m4a");
        tempFiles.Add(audioFile);

        steps.Add(new FfmpegStep(
            "Building audio",
            FfmpegArgs.BuildAudio(
                audioSpans, audioInputs, project.Output.SampleRate, settings.AudioBitrateKbps, audioFile),
            totalSeconds,
            audioFile));

        steps.Add(new FfmpegStep(
            "Finalizing",
            FfmpegArgs.Mux(videoFile, audioFile, settings.OutputPath),
            Math.Max(1, totalSeconds * 0.05)));
    }

    /// <summary>
    /// Builds the kept audio ranges for the single-pass audio job.
    /// </summary>
    /// <remarks>
    /// Adjacent plan segments that read continuously from the same source are merged,
    /// because a crop or overlay boundary splits the video plan but does not interrupt the
    /// audio. Every avoided split is one fewer join, and every join is a potential click.
    /// </remarks>
    internal static List<AudioSpan> BuildAudioSpans(
        Project project,
        IReadOnlyList<FlatSegment> plan,
        Func<int, SourceIndex> indexOf,
        out List<string> inputPaths)
    {
        inputPaths = [];
        var inputIndexOf = new Dictionary<int, int>();
        var spans = new List<AudioSpan>();

        foreach (var segment in plan)
        {
            var source = project.RequireSource(segment.SourceId);
            if (!source.HasAudio) continue;

            if (!inputIndexOf.TryGetValue(segment.SourceId, out var inputIndex))
            {
                inputIndex = inputPaths.Count;
                inputIndexOf[segment.SourceId] = inputIndex;
                inputPaths.Add(source.Path);
            }

            var index = indexOf(segment.SourceId);
            var (start, end) = SourceSeconds(segment, index);

            if (spans.Count > 0)
            {
                var previous = spans[^1];
                if (previous.InputIndex == inputIndex && Math.Abs(previous.End - start) < 1e-6)
                {
                    spans[^1] = previous with { End = end };
                    continue;
                }
            }

            spans.Add(new AudioSpan(inputIndex, start, end));
        }

        return spans;
    }

    /// <summary>
    /// The source time range a plan segment reads, taken from the frame index rather than
    /// computed from a nominal rate — which is what keeps it correct under variable frame
    /// rate.
    /// </summary>
    private static (double Start, double End) SourceSeconds(FlatSegment segment, SourceIndex index)
    {
        var start = index.SecondsOf(segment.SourceStartFrame);

        var endFrame = segment.SourceStartFrame + segment.Timeline.Length;
        var end = endFrame < index.FrameCount
            ? index.SecondsOf(endFrame)
            : index.SecondsOf(index.FrameCount - 1) + (index.FrameCount > 1
                ? index.SecondsOf(index.FrameCount - 1) - index.SecondsOf(index.FrameCount - 2)
                : 0);

        return (start, end);
    }
}
