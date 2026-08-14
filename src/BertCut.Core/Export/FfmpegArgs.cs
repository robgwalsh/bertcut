using System.Globalization;
using BertCut.Core.Model;
using BertCut.Core.Timeline;

namespace BertCut.Core.Export;

/// <summary>
/// Builds ffmpeg argument lists. Pure — no process launching, no filesystem access.
/// </summary>
/// <remarks>
/// Arguments are produced as a list, never as a command line string. They are handed to
/// <c>ProcessStartInfo.ArgumentList</c>, which applies the Windows quoting rules
/// correctly. Filter graphs contain <c>: , ; [ ] '</c> and Windows drive colons, so
/// hand-quoting them is a reliable source of defects.
/// </remarks>
public static class FfmpegArgs
{
    /// <summary>Written to stdout as key=value blocks and parsed by <see cref="ProgressParser"/>.</summary>
    private static readonly string[] ProgressFlags =
        ["-progress", "pipe:1", "-nostats", "-loglevel", "error", "-stats_period", "0.25"];

    private static readonly string[] Preamble = ["-hide_banner", "-nostdin", "-y"];

    /// <summary>
    /// Copies one kept range out of a source without re-encoding video.
    /// </summary>
    /// <remarks>
    /// <paramref name="start"/> and <paramref name="end"/> are placed <em>before</em>
    /// <c>-i</c>. As input options they are absolute positions in the source, which is
    /// what a cut list describes. Placed after <c>-i</c>, <c>-to</c> would instead be
    /// relative to the output timeline — a classic and quiet source of wrong cuts.
    ///
    /// Video only: audio is rebuilt in one continuous pass, because AAC frames are 1024
    /// samples (21.33 ms at 48 kHz) and never align to video frame boundaries, so copying
    /// audio would leave every cut up to 20 ms out of sync.
    /// </remarks>
    public static List<string> CopySegment(string sourcePath, double start, double end, string outputPath)
    {
        List<string> args = [.. Preamble];
        args.AddRange(["-ss", Seconds(start), "-to", Seconds(end), "-i", sourcePath]);
        args.AddRange(["-map", "0:v:0", "-c:v", "copy"]);
        args.AddRange(["-avoid_negative_ts", "make_zero"]);
        args.AddRange(ProgressFlags);
        args.Add(outputPath);
        return args;
    }

    /// <summary>
    /// Renders one flat segment: exact trim, optional crop-and-zoom, optional overlay.
    /// </summary>
    /// <remarks>
    /// Input-side <c>-ss</c> with re-encoding is both frame-accurate and seek-efficient:
    /// ffmpeg seeks to the preceding keyframe, decodes, and discards up to the exact
    /// timestamp. That is why export runs one process per kept segment rather than one
    /// <c>filter_complex</c> with <c>trim</c> filters over the whole file — the latter
    /// decodes the entire source and throws most of it away, so cutting five minutes out
    /// of a two-hour recording would decode two hours.
    /// </remarks>
    public static List<string> RenderSegment(
        FlatSegment segment,
        SegmentTiming timing,
        OutputFormat output,
        string basePath,
        string? overlayPath,
        VideoEncoder encoder,
        Quality quality,
        bool useCudaDecode,
        string outputPath)
    {
        List<string> args = [.. Preamble];

        if (useCudaDecode) args.AddRange(["-hwaccel", "cuda"]);
        args.AddRange(["-ss", Seconds(timing.BaseStart), "-to", Seconds(timing.BaseEnd), "-i", basePath]);

        var hasOverlay = segment.Overlay is not null && overlayPath is not null;
        if (hasOverlay)
        {
            if (useCudaDecode) args.AddRange(["-hwaccel", "cuda"]);
            args.AddRange(["-ss", Seconds(timing.OverlayStart), "-i", overlayPath!]);
        }

        args.AddRange(["-filter_complex", BuildFilterGraph(segment, output, hasOverlay)]);
        args.AddRange(["-map", "[vout]"]);
        args.AddRange(VideoEncoderArgs(encoder, quality));
        args.AddRange(["-fps_mode", "cfr", "-r", output.FrameRate.ToString()]);
        args.AddRange(ProgressFlags);
        args.Add(outputPath);

        return args;
    }

    /// <summary>
    /// Builds the whole output's audio in a single pass: trim each kept range, restamp it,
    /// concatenate, and resample.
    /// </summary>
    /// <remarks>
    /// Doing audio in one pass rather than per segment is what avoids a click at every
    /// join. <c>asetpts=PTS-STARTPTS</c> on each piece is mandatory; omitting it is the
    /// single most common cause of audio drifting after the second cut, because
    /// <c>concat</c> would otherwise see the original timestamps and leave gaps.
    /// </remarks>
    public static List<string> BuildAudio(
        IReadOnlyList<AudioSpan> spans,
        IReadOnlyList<string> inputPaths,
        int sampleRate,
        int bitrateKbps,
        string outputPath)
    {
        List<string> args = [.. Preamble];
        foreach (var path in inputPaths) args.AddRange(["-i", path]);

        var graph = new System.Text.StringBuilder();
        for (var i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            graph.Append(CultureInfo.InvariantCulture, $"[{s.InputIndex}:a]");
            graph.Append(CultureInfo.InvariantCulture, $"atrim=start={Seconds(s.Start)}:end={Seconds(s.End)},");
            graph.Append("asetpts=PTS-STARTPTS,");
            graph.Append(CultureInfo.InvariantCulture,
                $"aformat=sample_fmts=fltp:sample_rates={sampleRate}:channel_layouts=stereo");
            graph.Append(CultureInfo.InvariantCulture, $"[a{i}];");
        }

        for (var i = 0; i < spans.Count; i++) graph.Append(CultureInfo.InvariantCulture, $"[a{i}]");
        graph.Append(CultureInfo.InvariantCulture, $"concat=n={spans.Count}:v=0:a=1[acat];");

        // async=1 corrects drift by inserting or dropping samples instead of letting it
        // accumulate; first_pts=0 pads the start so audio and video both begin at zero.
        graph.Append("[acat]aresample=async=1:first_pts=0[aout]");

        args.AddRange(["-filter_complex", graph.ToString()]);
        args.AddRange(["-map", "[aout]"]);
        args.AddRange(["-c:a", "aac", "-b:a", $"{bitrateKbps}k", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "2"]);
        args.AddRange(ProgressFlags);
        args.Add(outputPath);

        return args;
    }

    /// <summary>Joins pre-rendered segments at the packet level, without re-encoding.</summary>
    public static List<string> ConcatSegments(string listFilePath, string outputPath)
    {
        List<string> args = [.. Preamble];

        // -safe 0 is required for absolute Windows paths in the list file.
        args.AddRange(["-f", "concat", "-safe", "0", "-i", listFilePath]);
        args.AddRange(["-map", "0", "-c", "copy", "-fflags", "+genpts"]);
        args.AddRange(ProgressFlags);
        args.Add(outputPath);

        return args;
    }

    /// <summary>Muxes the finished video and audio tracks into the delivered file.</summary>
    public static List<string> Mux(string videoPath, string audioPath, string outputPath)
    {
        List<string> args = [.. Preamble];
        args.AddRange(["-i", videoPath]);
        args.AddRange(["-i", audioPath]);
        args.AddRange(["-map", "0:v:0", "-map", "1:a:0", "-c", "copy"]);
        args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(ProgressFlags);
        args.Add(outputPath);
        return args;
    }

    /// <summary>Muxes video with no audio track.</summary>
    public static List<string> Finalize(string videoPath, string outputPath)
    {
        List<string> args = [.. Preamble];
        args.AddRange(["-i", videoPath, "-map", "0:v:0", "-c", "copy", "-movflags", "+faststart"]);
        args.AddRange(ProgressFlags);
        args.Add(outputPath);
        return args;
    }

    /// <summary>The content of a concat demuxer list file.</summary>
    public static string ConcatListFile(IEnumerable<string> paths)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("ffconcat version 1.0\n");
        foreach (var path in paths)
            sb.Append(CultureInfo.InvariantCulture, $"file '{path.Replace("'", @"'\''")}'\n");
        return sb.ToString();
    }

    /// <summary>
    /// The filter chain for one segment: crop-and-zoom on the base, then the overlay.
    /// </summary>
    internal static string BuildFilterGraph(FlatSegment segment, OutputFormat output, bool hasOverlay)
    {
        var graph = new System.Text.StringBuilder();
        graph.Append("[0:v]");

        if (segment.Crop is { } crop)
        {
            // Crop rects are aspect-locked to the output, so this scale never needs a pad.
            // That missing branch is one fewer place the preview and the export can differ.
            graph.Append(CultureInfo.InvariantCulture, $"crop={crop.W}:{crop.H}:{crop.X}:{crop.Y},");
        }

        graph.Append(CultureInfo.InvariantCulture,
            $"scale={output.Width}:{output.Height}:flags=lanczos,setsar=1,format=yuv420p");

        if (!hasOverlay)
        {
            graph.Append("[vout]");
            return graph.ToString();
        }

        var dest = segment.Overlay!.Value.Dest;
        graph.Append("[vbase];");
        graph.Append(CultureInfo.InvariantCulture,
            $"[1:v]scale={dest.W}:{dest.H}:flags=lanczos,setsar=1,format=yuv420p[vpip];");

        // eof_action=pass leaves the base visible if the overlay source runs out; the
        // default repeats its last frame, which reads as a freeze.
        graph.Append(CultureInfo.InvariantCulture,
            $"[vbase][vpip]overlay=x={dest.X}:y={dest.Y}:eof_action=pass[vout]");

        return graph.ToString();
    }

    private static string[] VideoEncoderArgs(VideoEncoder encoder, Quality quality) => encoder switch
    {
        VideoEncoder.H264Nvenc =>
        [
            "-c:v", "h264_nvenc",
            "-preset", quality switch { Quality.Fast => "p4", Quality.High => "p7", _ => "p6" },
            "-tune", "hq",
            "-rc", "vbr",
            "-cq", quality switch { Quality.Fast => "24", Quality.High => "19", _ => "21" },

            // Mandatory with -cq. Without it NVENC's default 2 Mbps target silently
            // overrides the quality setting and every export comes out at 2 Mbps.
            "-b:v", "0",
            "-maxrate", "40M", "-bufsize", "80M",
            "-multipass", "fullres",
            "-spatial-aq", "1", "-temporal-aq", "1",
            "-rc-lookahead", "32",
            "-bf", "3", "-b_ref_mode", "middle",
            "-profile:v", "high", "-pix_fmt", "yuv420p",
        ],

        VideoEncoder.H264Qsv =>
        [
            "-c:v", "h264_qsv",
            "-preset", quality switch { Quality.Fast => "veryfast", Quality.High => "veryslow", _ => "medium" },
            "-global_quality", quality switch { Quality.Fast => "26", Quality.High => "20", _ => "23" },
            "-pix_fmt", "nv12",
        ],

        VideoEncoder.H264Amf =>
        [
            "-c:v", "h264_amf",
            "-quality", quality switch { Quality.Fast => "speed", Quality.High => "quality", _ => "balanced" },
            "-rc", "cqp",
            "-qp_i", "22", "-qp_p", "24",
            "-pix_fmt", "yuv420p",
        ],

        VideoEncoder.Libx264 =>
        [
            "-c:v", "libx264",
            "-preset", quality switch { Quality.Fast => "veryfast", Quality.High => "slow", _ => "medium" },
            "-crf", quality switch { Quality.Fast => "24", Quality.High => "19", _ => "21" },
            "-profile:v", "high", "-pix_fmt", "yuv420p",
        ],

        VideoEncoder.Libopenh264 =>
        [
            "-c:v", "libopenh264",
            "-b:v", quality switch { Quality.Fast => "4M", Quality.High => "12M", _ => "8M" },
            "-pix_fmt", "yuv420p",
        ],

        _ => throw new ArgumentOutOfRangeException(nameof(encoder)),
    };

    /// <summary>
    /// Formats a time for ffmpeg with microsecond resolution and an invariant decimal
    /// point — a comma from a European locale would be parsed as an argument separator.
    /// </summary>
    private static string Seconds(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>Where one rendered segment reads from, in source seconds.</summary>
public readonly record struct SegmentTiming(double BaseStart, double BaseEnd, double OverlayStart);

/// <summary>One kept range of audio, in source seconds.</summary>
public readonly record struct AudioSpan(int InputIndex, double Start, double End);
