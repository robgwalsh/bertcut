using System.Diagnostics;
using BertCut.Core.Audio;
using BertCut.Core.Edits;
using BertCut.Core.Model;
using BertCut.Core.Session;
using BertCut.Core.Time;
using BertCut.Media;
using BertCut.Media.Audio;
using BertCut.Media.Decode;

namespace BertCut.Core.Tests;

/// <summary>
/// The driving use case, end to end: one recording holding the same event from two angles,
/// and an overlay of the second angle snapped onto the first by its sound.
/// </summary>
/// <remarks>
/// <see cref="AudioSyncTests"/> proves the correlation on planted offsets and
/// <see cref="AudioPeaksTests"/> proves the decode; this proves they compose — through a
/// real file, a real probe, the real timestamp table, and the same
/// <see cref="OverlaySync.Solve"/> the editor calls.
/// </remarks>
[Collection("ffmpeg")]
public class OverlaySyncTests : IDisposable
{
    private const int Fps = 30;
    private const int SampleRate = 48000;
    private const int AngleSeconds = 6;

    private readonly FfmpegRuntime? _runtime;
    private readonly string _dir;
    private readonly string? _previousStateDir;

    public OverlaySyncTests()
    {
        try
        {
            _runtime = FfmpegRuntime.Locate();
            if (_runtime is not null) FfmpegLoader.EnsureInitialized(_runtime);
        }
        catch (FileNotFoundException)
        {
            _runtime = null;
        }

        _dir = Path.Combine(Path.GetTempPath(), "bertcut-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _previousStateDir = Environment.GetEnvironmentVariable(AppPaths.OverrideVariable);
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, Path.Combine(_dir, "state"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, _previousStateDir);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [SkippableFact]
    public async Task An_overlay_of_the_second_angle_snaps_onto_the_first()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, indices) = await TwoAngleProjectAsync("angles.mp4");

        // Overlay a slice of the timeline that sits inside the first angle, taking its
        // content from the same file — which is where the identity trap lives.
        var range = new FrameRange(15, 165);
        const long wrongStart = 0;

        var outcome = OverlaySync.Solve(
            project, range, overlaySourceId: 1, currentSourceStartFrame: wrongStart,
            indexOf: id => indices[id], peaksOf: PeaksOf(project));

        Assert.True(outcome.Succeeded, $"sync failed: {outcome.Failure}");

        // Frame 15 of the first angle is 0.5 s in; the same instant of the second angle is
        // 6.5 s in, which is frame 195.
        Assert.InRange(outcome.SourceStartFrame, 195 - 1, 195 + 1);
        Assert.True(outcome.Confidence > OverlaySync.MinimumConfidence,
            $"confidence was {outcome.Confidence:0.000}");
    }

    [SkippableFact]
    public async Task Syncing_an_already_synced_overlay_leaves_it_where_it_is()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, indices) = await TwoAngleProjectAsync("idempotent.mp4");
        var range = new FrameRange(15, 165);

        var first = OverlaySync.Solve(
            project, range, 1, 0, id => indices[id], PeaksOf(project));

        Assert.True(first.Succeeded, $"first sync failed: {first.Failure}");

        var second = OverlaySync.Solve(
            project, range, 1, first.SourceStartFrame, id => indices[id], PeaksOf(project));

        Assert.True(second.Succeeded, $"second sync failed: {second.Failure}");
        Assert.Equal(first.SourceStartFrame, second.SourceStartFrame);
    }

    [SkippableFact]
    public async Task A_range_too_short_to_identify_a_moment_is_refused()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, indices) = await TwoAngleProjectAsync("short.mp4");

        var outcome = OverlaySync.Solve(
            project, new FrameRange(15, 25), 1, 0, id => indices[id], PeaksOf(project));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SyncFailure.RangeTooShort, outcome.Failure);
    }

    [SkippableFact]
    public async Task A_source_with_no_audio_is_reported_rather_than_guessed_at()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var path = Path.Combine(_dir, "silent.mp4");
        Run(_runtime!.FfmpegPath,
        [
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", $"testsrc2=size=320x240:rate={Fps}:duration=6",
            "-c:v", "libopenh264", "-b:v", "1M", "-g", "30", "-pix_fmt", "yuv420p",
            "-fps_mode", "cfr", "-r", Fps.ToString(),
            path,
        ]);

        var probe = await new MediaProber(_runtime).ProbeAsync(path);
        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(320, 240, Rational.FromInt(Fps), SampleRate)), probe.Media);

        var outcome = OverlaySync.Solve(
            project, new FrameRange(0, 100), 1, 0, _ => probe.Index, _ => null);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SyncFailure.NoAudio, outcome.Failure);
    }

    [SkippableFact]
    public async Task The_timeline_reader_plays_the_cut_timeline_and_not_the_source()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, indices) = await TwoAngleProjectAsync("reader.mp4");

        // Drop the first angle. Timeline frame 0 must now be the second angle's audio, which
        // is the same test the preview compositor makes for pixels.
        var cut = TimelineEdits.RippleDelete(project, new FrameRange(0, AngleSeconds * Fps));

        using var reader = new TimelineAudioReader(cut, id => indices[id]);
        reader.SeekToFrame(0);
        Assert.Equal(0, reader.PositionFrames);

        var frames = SampleRate / 2;
        var buffer = new float[frames * reader.Channels];
        var read = reader.Read(buffer, 0, frames);

        Assert.True(read > 0, "the reader produced no audio");

        // The second angle is the quieter one, and the first half second of the first angle
        // is loud, so a level well under the original proves the cut was honoured.
        Assert.InRange(MeanLevel(buffer, read * reader.Channels), 0.001, 0.35);

        // Half a second of audio is half a second of timeline: this is the mapping the
        // playback clock inverts, so it has to hold exactly.
        Assert.Equal(Fps / 2, reader.PositionFrames);
    }

    // ---- fixtures -----------------------------------------------------------------

    private Func<int, AudioPeaks?> PeaksOf(Project project) => id =>
        project.FindSource(id) is { } source
            ? AudioPeaksCache.GetOrBuild(source.Path, source.ContentKey, SampleRate)
            : null;

    /// <summary>
    /// Builds a project over one file holding the same event twice.
    /// </summary>
    private async Task<(Project Project, Dictionary<int, Core.Media.SourceIndex> Indices)>
        TwoAngleProjectAsync(string name)
    {
        var path = MakeTwoAngleClip(name);
        var probe = await new MediaProber(_runtime!).ProbeAsync(path);

        Assert.True(probe.Media.HasAudio, "the fixture should have an audio track");

        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(320, 240, Rational.FromInt(Fps), SampleRate)), probe.Media);

        return (project, new Dictionary<int, Core.Media.SourceIndex> { [1] = probe.Index });
    }

    /// <summary>
    /// Twelve seconds of video over an audio track that plays one event, then plays it again
    /// quieter and noisier — a second camera's take of the same thing.
    /// </summary>
    private string MakeTwoAngleClip(string name)
    {
        var wav = Path.Combine(_dir, Path.GetFileNameWithoutExtension(name) + ".wav");
        WriteTwoAngleWav(wav);

        var path = Path.Combine(_dir, name);

        Run(_runtime!.FfmpegPath,
        [
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", $"testsrc2=size=320x240:rate={Fps}:duration={AngleSeconds * 2}",
            "-i", wav,
            "-c:v", "libopenh264", "-b:v", "1M", "-g", "30", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k",
            "-fps_mode", "cfr", "-r", Fps.ToString(),
            "-shortest",
            path,
        ]);

        return path;
    }

    /// <summary>
    /// Writes the fixture's audio: a 440 Hz carrier under an aperiodic amplitude envelope,
    /// then the same envelope again at 60% with its own noise.
    /// </summary>
    /// <remarks>
    /// Generated here rather than with a lavfi expression so the envelope is exactly
    /// reproducible and provably aperiodic. A repeating envelope would correlate equally
    /// well at many offsets and every assertion above would be meaningless.
    /// </remarks>
    private static void WriteTwoAngleWav(string path)
    {
        var perAngle = AngleSeconds * SampleRate;
        var envelope = SmoothEnvelope(seed: 20260815, samples: perAngle);

        var noise = new Random(4242);
        var samples = new short[perAngle * 2];

        for (var i = 0; i < perAngle; i++)
        {
            var carrier = Math.Sin(2 * Math.PI * 440 * i / SampleRate);

            samples[i] = ToPcm(envelope[i] * carrier * 0.8);

            // The second angle: quieter, and with hiss of its own, so this is a match on the
            // shape of the sound rather than on identical samples.
            var second = (envelope[i] * carrier * 0.48) + ((noise.NextDouble() - 0.5) * 0.06);
            samples[perAngle + i] = ToPcm(second);
        }

        WriteWav(path, samples);
    }

    /// <summary>A slowly varying loudness curve that never repeats inside the clip.</summary>
    private static double[] SmoothEnvelope(int seed, int samples)
    {
        // Control points a tenth of a second apart, linearly interpolated — enough structure
        // for correlation to lock onto, without step changes that would ring through AAC.
        const int perPoint = SampleRate / 10;

        var random = new Random(seed);
        var points = new double[(samples / perPoint) + 2];
        for (var i = 0; i < points.Length; i++) points[i] = 0.15 + (random.NextDouble() * 0.85);

        var envelope = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var index = i / perPoint;
            var t = (i % perPoint) / (double)perPoint;
            envelope[i] = points[index] + ((points[index + 1] - points[index]) * t);
        }

        return envelope;
    }

    private static short ToPcm(double value) =>
        (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);

    private static void WriteWav(string path, short[] samples)
    {
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

    private static double MeanLevel(float[] buffer, int count)
    {
        double total = 0;
        for (var i = 0; i < count; i++) total += Math.Abs(buffer[i]);
        return count == 0 ? 0 : total / count;
    }

    private static void Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({process.ExitCode}): {stderr}");
    }
}
