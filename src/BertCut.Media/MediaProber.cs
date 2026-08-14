using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BertCut.Core.Media;
using BertCut.Core.Model;
using BertCut.Core.Time;

namespace BertCut.Media;

/// <summary>What an import produced.</summary>
public sealed record ProbeResult(SourceMedia Media, SourceIndex Index);

/// <summary>
/// Reads a media file's properties and builds its per-frame timestamp index.
/// </summary>
public sealed class MediaProber(FfmpegRuntime runtime)
{
    /// <summary>
    /// Probes a file, building its <see cref="SourceIndex"/>.
    /// </summary>
    /// <remarks>
    /// This is the step that makes variable frame rate a non-issue. Screen recorders emit
    /// VFR routinely, so a frame's timestamp cannot be computed from a nominal rate; the
    /// packet pass below records the real timestamps once, and everything above the
    /// decoder then works in exact frame indices.
    /// </remarks>
    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Source file not found.", path);

        var streams = await ReadStreamInfoAsync(path, cancellationToken).ConfigureAwait(false);
        var index = await ReadFrameIndexAsync(path, cancellationToken).ConfigureAwait(false);
        var contentKey = await ComputeContentKeyAsync(path, cancellationToken).ConfigureAwait(false);

        var nominalRate = streams.AverageFrameRate.Num > 0 ? streams.AverageFrameRate : Rational.FromInt(30);

        var media = new SourceMedia(
            Id: 0,
            Path: path,
            ContentKey: contentKey,
            FrameCount: index.FrameCount,
            Width: streams.Width,
            Height: streams.Height,
            FrameRate: nominalRate,
            IsVariableFrameRate: index.LooksVariableRate(),
            HasAudio: streams.HasAudio,
            AudioSampleRate: streams.AudioSampleRate,
            VideoCodec: streams.VideoCodec,
            PixelFormat: streams.PixelFormat);

        return new ProbeResult(media, index);
    }

    /// <summary>
    /// A stable identity for a file's contents: length, first 1 MiB, last 1 MiB.
    /// </summary>
    /// <remarks>
    /// Two constant-time reads regardless of file size, and stable across rename, move,
    /// and copy — so an autosaved session survives the user reorganising their recordings.
    /// Hashing the whole file would stall import for seconds on a 4K source; keying on
    /// path and mtime would break the moment anything is moved.
    /// </remarks>
    public static async Task<string> ComputeContentKeyAsync(string path, CancellationToken cancellationToken = default)
    {
        const int Window = 1024 * 1024;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Window, useAsync: true);

        using var sha = SHA256.Create();
        var length = stream.Length;

        sha.TransformBlock(BitConverter.GetBytes(length), 0, sizeof(long), null, 0);

        var buffer = new byte[Window];

        var head = await stream.ReadAtLeastAsync(buffer, (int)Math.Min(Window, length), false, cancellationToken)
            .ConfigureAwait(false);
        sha.TransformBlock(buffer, 0, head, null, 0);

        if (length > Window)
        {
            stream.Seek(Math.Max(Window, length - Window), SeekOrigin.Begin);
            var tail = await stream.ReadAtLeastAsync(buffer, 1, false, cancellationToken).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, tail, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    private async Task<StreamInfo> ReadStreamInfoAsync(string path, CancellationToken cancellationToken)
    {
        var json = await RunProbeAsync(
            ["-v", "error", "-show_streams", "-of", "json", path], cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams");

        JsonElement? video = null;
        JsonElement? audio = null;

        foreach (var stream in streams.EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
            if (video is null && type == "video") video = stream;
            if (audio is null && type == "audio") audio = stream;
        }

        if (video is null) throw new InvalidOperationException($"'{path}' has no video stream.");

        var v = video.Value;

        return new StreamInfo(
            Width: v.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
            Height: v.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
            AverageFrameRate: ParseRational(v, "avg_frame_rate") ?? ParseRational(v, "r_frame_rate") ?? Rational.FromInt(30),
            VideoCodec: v.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "",
            PixelFormat: v.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() ?? "" : "",
            HasAudio: audio is not null,
            AudioSampleRate: audio is not null && audio.Value.TryGetProperty("sample_rate", out var sr)
                && int.TryParse(sr.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate)
                ? rate
                : 48000);
    }

    /// <summary>
    /// Reads every video packet's timestamp and keyframe flag.
    /// </summary>
    /// <remarks>
    /// Packets rather than frames, because <c>-show_packets</c> does not decode — it only
    /// parses the container, so a two-hour recording indexes in a couple of seconds
    /// instead of minutes.
    /// </remarks>
    private async Task<SourceIndex> ReadFrameIndexAsync(string path, CancellationToken cancellationToken)
    {
        var timeBaseText = await RunProbeAsync(
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=time_base",
             "-of", "default=nw=1:nk=1", path],
            cancellationToken).ConfigureAwait(false);

        if (!Rational.TryParse(timeBaseText.Trim(), out var timeBase))
            timeBase = new Rational(1, 90000);

        var csv = await RunProbeAsync(
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags",
             "-of", "csv=p=0", path],
            cancellationToken).ConfigureAwait(false);

        var pts = new List<long>(4096);
        var keyFrames = new List<int>(256);

        foreach (var line in csv.Split('\n'))
        {
            var text = line.AsSpan().Trim();
            if (text.IsEmpty) continue;

            var comma = text.IndexOf(',');
            if (comma <= 0) continue;

            var timeText = text[..comma];
            if (timeText.SequenceEqual("N/A")) continue;
            if (!double.TryParse(timeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) continue;

            // 'K' in the flags column marks a keyframe.
            if (text[(comma + 1)..].IndexOf('K') >= 0) keyFrames.Add(pts.Count);

            pts.Add((long)Math.Round(seconds / timeBase.Approx));
        }

        if (pts.Count == 0) throw new InvalidOperationException($"'{path}' yielded no video packets.");

        // Packets are in decode order; presentation order is what the timeline needs.
        var ordered = pts.ToArray();
        var wasSorted = IsSorted(ordered);
        if (!wasSorted)
        {
            var keySet = keyFrames.Select(i => ordered[i]).ToHashSet();
            Array.Sort(ordered);
            keyFrames = [.. Enumerable.Range(0, ordered.Length).Where(i => keySet.Contains(ordered[i]))];
        }

        if (keyFrames.Count == 0 || keyFrames[0] != 0) keyFrames.Insert(0, 0);

        return new SourceIndex(timeBase, ordered, [.. keyFrames]);
    }

    private static bool IsSorted(long[] values)
    {
        for (var i = 1; i < values.Length; i++)
            if (values[i] < values[i - 1]) return false;
        return true;
    }

    private static Rational? ParseRational(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && Rational.TryParse(value.GetString() ?? "", out var rational)
        && rational.Num > 0
            ? rational
            : null;

    private async Task<string> RunProbeAsync(string[] args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(runtime.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffprobe.");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed ({process.ExitCode}): {await stderr.ConfigureAwait(false)}");

        return await stdout.ConfigureAwait(false);
    }

    private readonly record struct StreamInfo(
        int Width,
        int Height,
        Rational AverageFrameRate,
        string VideoCodec,
        string PixelFormat,
        bool HasAudio,
        int AudioSampleRate);
}
