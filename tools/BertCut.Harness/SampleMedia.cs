using System.Diagnostics;
using System.Globalization;
using BertCut.Media;

namespace BertCut.Harness;

/// <summary>
/// Synthesises a clip to drive the editor with.
/// </summary>
/// <remarks>
/// <para>
/// A script that depends on a file sitting on this machine is a script that runs here and
/// nowhere else, so the harness makes its own. <c>testsrc2</c> prints a frame counter and a
/// running clock into the picture, which means a capture of the preview says which frame it
/// is — the playhead can be checked by looking rather than only by asserting.
/// </para>
/// <para>
/// Encoded with <c>libopenh264</c> because <c>tools/fetch-ffmpeg.ps1</c> installs the LGPL
/// build, which has no libx264. Constant frame rate, so that a sample is the boring case and
/// variable-rate handling is exercised deliberately with a real recording instead.
/// </para>
/// <para>
/// The audio is generated here as a WAV rather than with a lavfi expression, because it has
/// to be <b>aperiodic</b>: a repeating envelope correlates equally well at many offsets, and
/// every audio-sync assertion made against it would be meaningless. Writing the samples in
/// C# also makes the two-angle fixture below exact — the second angle is the first one's
/// envelope, not something that merely resembles it.
/// </para>
/// </remarks>
internal static class SampleMedia
{
    private const int SampleRate = 48000;
    private const int Fps = 30;

    /// <summary>One clip with a picture-stamped frame counter and a sound to go with it.</summary>
    /// <remarks>
    /// 640x360 unless a script asks otherwise, which is the shape every existing one expects.
    /// A size is worth asking for when the question is about shapes — cutting a square region
    /// out of a 4:3 recording, say, which a 16:9 sample cannot pose.
    /// </remarks>
    public static void Write(
        FfmpegRuntime runtime, string path, int seconds, int width = 640, int height = 360)
    {
        var wav = Path.ChangeExtension(path, ".wav");
        WriteWav(wav, Event(seconds, seed: 1));

        Encode(runtime, path, wav, seconds, width, height);
    }

    /// <summary>
    /// One clip holding the same event twice: an angle, then the same angle again quieter
    /// and noisier, as a second camera would have heard it.
    /// </summary>
    /// <remarks>
    /// This is the shape the audio sync exists for — one recording of an event filmed twice,
    /// where the second half is to be cut out and overlaid onto the first. The identical
    /// envelope in both halves is what makes an assertion about where the sync landed
    /// meaningful, and the added noise is what stops it being a trivial exact match.
    /// </remarks>
    public static void WriteTwoAngles(FfmpegRuntime runtime, string path, int secondsPerAngle)
    {
        var first = Event(secondsPerAngle, seed: 7);
        var samples = new short[first.Length * 2];

        var noise = new Random(8);

        for (var i = 0; i < first.Length; i++)
        {
            samples[i] = first[i];
            samples[first.Length + i] = Pcm(
                (first[i] / (double)short.MaxValue * 0.6) + ((noise.NextDouble() - 0.5) * 0.05));
        }

        var wav = Path.ChangeExtension(path, ".wav");
        WriteWav(wav, samples);

        Encode(runtime, path, wav, secondsPerAngle * 2);
    }

    /// <summary>
    /// Two clips of one event, filmed by two cameras standing in different places and started
    /// <paramref name="apartSeconds"/> apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other shape the sync has to handle, and the harder one. <see cref="WriteTwoAngles"/>
    /// makes its second angle by scaling the first one's samples, so the two agree at every
    /// resolution — which is realistic for one recording holding two takes, and not at all
    /// realistic for two cameras. These hear the same event through different rooms and
    /// different microphones, so they agree about how loud it was from moment to moment and
    /// disagree about the waveform underneath, which is the case that used to be refused.
    /// </para>
    /// <para>
    /// They are separate files, so the identity exclusion is not in play: nothing is excluded
    /// and every offset in the candidate is admissible.
    /// </para>
    /// <para>
    /// The far camera starts <paramref name="apartSeconds"/> late and stops with the near one,
    /// so it is that much shorter — which also leaves it short enough to be aimed at a wrong
    /// place on the timeline before a sync is asked to find the right one.
    /// </para>
    /// </remarks>
    public static void WriteTwoCameras(
        FfmpegRuntime runtime, string near, string far, int seconds, int apartSeconds = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(seconds, apartSeconds);

        var world = Event(seconds, seed: 11);
        var farSeconds = seconds - apartSeconds;

        var wavNear = Path.ChangeExtension(near, ".wav");
        var wavFar = Path.ChangeExtension(far, ".wav");

        WriteWav(wavNear, Heard(
            world, 0, seconds, Near, smoothing: 0.5, gain: 0.85, hiss: 0.008, seed: 21));

        WriteWav(wavFar, Heard(
            world, apartSeconds * SampleRate, farSeconds, Far,
            smoothing: 0.08, gain: 0.45, hiss: 0.05, seed: 22));

        Encode(runtime, near, wavNear, seconds);
        Encode(runtime, far, wavFar, farSeconds);
    }

    /// <summary>The room the near camera is standing in: close, with one early reflection.</summary>
    private static readonly (int Delay, double Gain)[] Near =
    [
        (SampleRate * 13 / 1000, 0.25),
    ];

    /// <summary>
    /// The room the far camera is standing in, as a handful of reflections.
    /// </summary>
    /// <remarks>
    /// The early cluster is the part that matters. Arriving within a few milliseconds of the
    /// direct sound, it rewrites the waveform inside a one-millisecond bucket while leaving the
    /// loudness over ten milliseconds very nearly alone — which is the whole difference between
    /// what the coarse pass of a sync measures and what the fine pass does.
    /// </remarks>
    private static readonly (int Delay, double Gain)[] Far =
    [
        (SampleRate * 3 / 1000, 0.55),
        (SampleRate * 5 / 1000, 0.48),
        (SampleRate * 8 / 1000, 0.40),
        (SampleRate * 11 / 1000, 0.34),
        (SampleRate * 31 / 1000, 0.26),
        (SampleRate * 57 / 1000, 0.18),
    ];

    /// <summary>What one camera made of the sound in the room.</summary>
    /// <param name="smoothing">
    /// A one-pole low-pass coefficient, so the two microphones do not share a frequency
    /// response either. The far one is duller, which is what standing further away sounds like.
    /// </param>
    private static short[] Heard(
        short[] world, int start, int seconds, (int Delay, double Gain)[] room,
        double smoothing, double gain, double hiss, int seed)
    {
        var noise = new Random(seed);
        var result = new short[seconds * SampleRate];
        var low = 0.0;

        for (var i = 0; i < result.Length; i++)
        {
            var n = start + i;

            var wet = world[n] / (double)short.MaxValue;
            foreach (var (delay, level) in room)
                if (n >= delay) wet += world[n - delay] / (double)short.MaxValue * level;

            low += smoothing * (wet - low);

            result[i] = Pcm((low * gain) + ((noise.NextDouble() - 0.5) * hiss * 2));
        }

        return result;
    }

    /// <summary>
    /// A 440 Hz tone under a slowly varying, non-repeating loudness curve.
    /// </summary>
    /// <remarks>
    /// The envelope is a linearly interpolated random walk at ten points a second. Slow
    /// enough to survive AAC, structured enough for a correlation to lock onto, and — because
    /// it is a walk rather than a waveform — it never repeats within a clip.
    /// </remarks>
    private static short[] Event(int seconds, int seed)
    {
        var count = seconds * SampleRate;
        const int perPoint = SampleRate / 10;

        var random = new Random(seed);
        var points = new double[(count / perPoint) + 2];
        for (var i = 0; i < points.Length; i++) points[i] = 0.15 + (random.NextDouble() * 0.85);

        var samples = new short[count];

        for (var i = 0; i < count; i++)
        {
            var index = i / perPoint;
            var t = (i % perPoint) / (double)perPoint;
            var envelope = points[index] + ((points[index + 1] - points[index]) * t);

            samples[i] = Pcm(envelope * Math.Sin(2 * Math.PI * 440 * i / SampleRate) * 0.7);
        }

        return samples;
    }

    private static short Pcm(double value) =>
        (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);

    private static void WriteWav(string path, short[] samples)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        var dataBytes = samples.Length * sizeof(short);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                          // PCM header length
        writer.Write((short)1);                    // PCM
        writer.Write((short)1);                    // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));  // byte rate
        writer.Write((short)sizeof(short));        // block align
        writer.Write((short)16);                   // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples) writer.Write(sample);
    }

    /// <summary>
    /// Encodes one picture-and-sound pair, at a size the caller may choose.
    /// </summary>
    /// <remarks>
    /// The size is an argument because 640x360 cannot pose the question a region asks. Cutting
    /// a square out of a 4:3 recording and laying it over the video is the case the overlay
    /// crop exists for, and on a source whose shape is already the output's, a canvas that
    /// quietly stayed the output's would look exactly like one that took the source's.
    /// </remarks>
    private static void Encode(
        FfmpegRuntime runtime, string path, string wav, int seconds,
        int width = 640, int height = 360)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        var start = new ProcessStartInfo(runtime.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
        {
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i",
            $"testsrc2=size={width}x{height}:rate={Fps}:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
            "-i", wav,
            "-c:v", "libopenh264", "-b:v", "4M", "-g", "30", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k",
            "-fps_mode", "cfr", "-r", Fps.ToString(CultureInfo.InvariantCulture),
            "-shortest",
            path,
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {runtime.FfmpegPath}.");

        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg could not write the sample: {errors}");

        // The WAV was scaffolding; leaving it beside the clip would confuse a later 'open'.
        try { File.Delete(wav); } catch (IOException) { /* best effort */ }
    }

    /// <summary>
    /// A silent GIF, at a rate that is neither the project's nor a whole number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixture for importing a GIF as a segment, and deliberately awkward on both counts a
    /// GIF is awkward: it carries no audio, and its rate does not divide the timeline's. Twelve
    /// frames a second in a 1/100 time base gives 12.5 as the average rate ffprobe reports, so a
    /// clip of it lands on a fractional number of timeline frames and has to be floored — which
    /// is the arithmetic that <c>ProjectInvariants</c> used to reject outright.
    /// </para>
    /// <para>
    /// Two passes, because one is visibly worse: <c>palettegen</c> chooses 256 colours for the
    /// whole clip and <c>paletteuse</c> maps to them, where letting the GIF encoder pick per
    /// frame bands every gradient in the picture.
    /// </para>
    /// </remarks>
    public static void WriteGif(FfmpegRuntime runtime, string path, int seconds)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        var duration = seconds.ToString(CultureInfo.InvariantCulture);
        var source = $"testsrc2=size=320x240:rate=15:duration={duration}";
        var palette = Path.ChangeExtension(path, ".palette.png");

        RunFfmpeg(runtime,
        [
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", source,
            "-vf", "fps=12,scale=320:-1:flags=lanczos,palettegen=stats_mode=diff",
            palette,
        ]);

        RunFfmpeg(runtime,
        [
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", source,
            "-i", palette,
            "-lavfi",
            "fps=12,scale=320:-1:flags=lanczos[x];" +
            "[x][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
            path,
        ]);

        try { File.Delete(palette); } catch (IOException) { /* best effort */ }
    }

    /// <summary>Runs ffmpeg and throws with its diagnostics when it fails.</summary>
    private static void RunFfmpeg(FfmpegRuntime runtime, string[] arguments)
    {
        var start = new ProcessStartInfo(runtime.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {runtime.FfmpegPath}.");

        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg could not write the sample: {errors}");
    }
}
