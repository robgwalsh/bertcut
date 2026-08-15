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
    public static void Write(FfmpegRuntime runtime, string path, int seconds)
    {
        var wav = Path.ChangeExtension(path, ".wav");
        WriteWav(wav, Event(seconds, seed: 1));

        Encode(runtime, path, wav, seconds);
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

    private static void Encode(FfmpegRuntime runtime, string path, string wav, int seconds)
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
            $"testsrc2=size=640x360:rate={Fps}:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
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
}
