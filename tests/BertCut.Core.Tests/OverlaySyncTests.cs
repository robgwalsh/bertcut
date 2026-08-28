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

    /// <summary>How long each of the two cameras rolls for.</summary>
    private const int CameraSeconds = 12;

    /// <summary>How much later the second camera started recording.</summary>
    private const int CameraOffsetSeconds = 4;

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

    // ---- the other direction: the content is fixed, and the clip is what moves ----

    [SkippableFact]
    public async Task A_clip_of_the_second_angle_is_placed_over_the_first()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, indices) = await TwoAngleProjectAsync("place.mp4");

        // Five seconds of the second angle, taken from source frame 195 — the same instant
        // frame 15 of the first angle shows. Asked for from entirely the wrong place.
        var outcome = OverlaySync.SolveTimelinePosition(
            project, overlaySourceId: 1, overlaySourceStartFrame: 195, lengthFrames: 150,
            currentTimelineStart: 250, indexOf: id => indices[id], peaksOf: PeaksOf(project));

        Assert.True(outcome.Succeeded, $"placement failed: {outcome.Failure}");

        // The base is uncut, so the timeline frame is the source frame.
        Assert.InRange(outcome.TimelineStartFrame, 15 - 2, 15 + 2);
        Assert.True(outcome.Confidence > OverlaySync.MinimumConfidence,
            $"confidence was {outcome.Confidence:0.000}");
    }

    [SkippableFact]
    public async Task Placing_an_already_placed_clip_leaves_it_where_it_is()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, indices) = await TwoAngleProjectAsync("place-again.mp4");

        var first = OverlaySync.SolveTimelinePosition(
            project, 1, 195, 150, 250, id => indices[id], PeaksOf(project));

        Assert.True(first.Succeeded, $"first placement failed: {first.Failure}");

        var second = OverlaySync.SolveTimelinePosition(
            project, 1, 195, 150, first.TimelineStartFrame, id => indices[id], PeaksOf(project));

        Assert.True(second.Succeeded, $"second placement failed: {second.Failure}");
        Assert.Equal(first.TimelineStartFrame, second.TimelineStartFrame);
    }

    [SkippableFact]
    public async Task A_match_on_footage_that_was_cut_away_is_reported_rather_than_rounded()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, indices) = await TwoAngleProjectAsync("cut-away.mp4");

        // Ripple the first angle out of the timeline. The second angle's content still matches
        // it in the *file*, but there is nowhere on the timeline left to put the clip — and
        // snapping to the nearest surviving frame would be confidently wrong by six seconds.
        var cut = TimelineEdits.RippleDelete(project, new FrameRange(0, AngleSeconds * Fps));

        var outcome = OverlaySync.SolveTimelinePosition(
            cut, 1, 195, 150, 0, id => indices[id], PeaksOf(cut));

        Assert.False(outcome.Succeeded);
        Assert.Equal(SyncFailure.MatchNotOnTimeline, outcome.Failure);
    }

    /// <summary>
    /// Two cameras of one event agree about loudness and disagree about waveform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the measurement the two-pass split rests on, and it is here because it is easy
    /// to build a fixture that hides it. The other fixture in this file makes its second angle
    /// by scaling the first one's samples, so the two agree at every resolution and a
    /// correlation run at 1 ms buckets sails through. A real second camera stands somewhere
    /// else: it hears its own room, through its own microphone, riding its own automatic gain.
    /// </para>
    /// <para>
    /// So the coarse pass over the 100 Hz envelopes still recognises the event, and correlating
    /// near-waveform detail at 1 kHz scores far lower on the very same alignment. Gating on the
    /// second number is what used to refuse footage the first number had already identified.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Two_cameras_agree_on_loudness_far_better_than_on_waveform()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, _) = await TwoCameraProjectAsync("loud-a.mp4", "loud-b.mp4");

        var near = project.RequireSource(1);
        var far = project.RequireSource(2);

        var nearPeaks = AudioPeaksCache.GetOrBuild(near.Path, near.ContentKey, SampleRate)!;
        var farPeaks = AudioPeaksCache.GetOrBuild(far.Path, far.ContentKey, SampleRate)!;

        // The same five seconds of the event: six seconds into the near camera, and two into
        // the far one, which started rolling four seconds later.
        const double referenceStart = 6;
        const double referenceLength = 5;
        const double answer = referenceStart - CameraOffsetSeconds;

        var coarse = AudioSync.FindOffsets(
            new SyncRequest(nearPeaks, referenceStart, referenceLength, farPeaks));

        Assert.NotEmpty(coarse);
        Assert.InRange(coarse[0].OffsetSeconds, answer - 0.05, answer + 0.05);

        var fine = ConfidenceAt(near.Path, far.Path, referenceStart, 4, answer, rate: 1000);

        Assert.True(coarse[0].Confidence > OverlaySync.MinimumConfidence,
            $"the loudness shape survives a second microphone, but scored {coarse[0].Confidence:0.000}");

        Assert.True(fine < coarse[0].Confidence - 0.1,
            $"waveform detail should be much the weaker signal, but coarse scored "
            + $"{coarse[0].Confidence:0.000} and fine {fine:0.000}");
    }

    /// <summary>
    /// A clip from a second camera lands on the base track, which is the case this whole
    /// feature exists for and the one nothing in this file used to cover.
    /// </summary>
    /// <remarks>
    /// Two files, so <c>sameFile</c> is false, nothing is excluded and every offset in the
    /// candidate is admissible — a different path through <see cref="OverlaySync"/> from the
    /// one-recording-two-angles tests above.
    /// </remarks>
    [SkippableFact]
    public async Task An_overlay_from_a_second_camera_snaps_onto_the_base()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, indices) = await TwoCameraProjectAsync("cam-a.mp4", "cam-b.mp4");

        // Five seconds of the programme starting six seconds in. The far camera started four
        // seconds later, so the same moment is two seconds into its own recording.
        var range = new FrameRange(6 * Fps, 11 * Fps);
        const long expected = (6 - CameraOffsetSeconds) * Fps;

        var outcome = OverlaySync.Solve(
            project, range, overlaySourceId: 2, currentSourceStartFrame: 0,
            indexOf: id => indices[id], peaksOf: PeaksOf(project));

        Assert.True(outcome.Succeeded, $"sync failed: {outcome.Failure}");
        Assert.InRange(outcome.SourceStartFrame, expected - 1, expected + 1);
    }

    /// <summary>
    /// The field of offers leads with the one that was applied, and the rest are behind it.
    /// </summary>
    /// <remarks>
    /// This is the contract the "take it anyway" key rests on. The solver ranks what it found
    /// and hands the whole field back, so a caller can apply the best of it without asking
    /// again and can walk past it when the best was the wrong one — including on the refusals,
    /// where the field is the only thing standing between the user and a flat no.
    /// </remarks>
    [SkippableFact]
    public async Task The_offers_lead_with_the_one_that_was_applied()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var (project, indices) = await TwoCameraProjectAsync("offers-a.mp4", "offers-b.mp4");

        var outcome = OverlaySync.Solve(
            project, new FrameRange(6 * Fps, 11 * Fps), overlaySourceId: 2,
            currentSourceStartFrame: 0, indexOf: id => indices[id], peaksOf: PeaksOf(project));

        Assert.True(outcome.Succeeded, $"sync failed: {outcome.Failure}");
        Assert.NotEmpty(outcome.Matches);
        Assert.Equal(outcome.SourceStartFrame, outcome.Matches[0].Frame);
        Assert.Equal(outcome.Confidence, outcome.Matches[0].Confidence);

        // Ranked, so walking down the field only ever gives up confidence.
        for (var i = 1; i < outcome.Matches.Count; i++)
            Assert.True(outcome.Matches[i].Confidence <= outcome.Matches[i - 1].Confidence,
                $"offer {i} scored {outcome.Matches[i].Confidence:0.000}, above the one before it");
    }

    /// <summary>A default outcome answers an empty field rather than a null one.</summary>
    /// <remarks>
    /// <c>Matches</c> is backed by a nullable field precisely so that <c>default</c> — which a
    /// struct can always be made — does not hand a caller a null list to walk.
    /// </remarks>
    [Fact]
    public void A_refusal_carrying_nothing_still_answers_an_empty_field()
    {
        Assert.Empty(default(SyncOutcome).Matches);
        Assert.Empty(default(TimelineSyncOutcome).Matches);
        Assert.Empty(SyncOutcome.Failed(SyncFailure.NoAudio).Matches);
        Assert.Empty(TimelineSyncOutcome.Failed(SyncFailure.NoAudio).Matches);
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

    /// <summary>Builds a project over two cameras' recordings of one event.</summary>
    /// <remarks>
    /// The near camera is the base track; the far one is imported as a source to overlay from
    /// rather than as another stretch of the programme, which is what <c>appendToBase: false</c>
    /// says.
    /// </remarks>
    private async Task<(Project Project, Dictionary<int, Core.Media.SourceIndex> Indices)>
        TwoCameraProjectAsync(string nameA, string nameB)
    {
        var (pathA, pathB) = MakeTwoCameraClips(nameA, nameB);

        var prober = new MediaProber(_runtime!);
        var a = await prober.ProbeAsync(pathA);
        var b = await prober.ProbeAsync(pathB);

        Assert.True(a.Media.HasAudio && b.Media.HasAudio, "both fixtures should have audio");

        var project = TimelineEdits.ImportSource(
            Project.Empty(new OutputFormat(320, 240, Rational.FromInt(Fps), SampleRate)), a.Media);

        project = TimelineEdits.ImportSource(project, b.Media, appendToBase: false);

        return (project, new Dictionary<int, Core.Media.SourceIndex> { [1] = a.Index, [2] = b.Index });
    }

    /// <summary>
    /// One event, recorded by two cameras standing in different places and started
    /// <see cref="CameraOffsetSeconds"/> apart.
    /// </summary>
    private (string Near, string Far) MakeTwoCameraClips(string nameA, string nameB)
    {
        var world = WorldEvent();

        var wavA = Path.Combine(_dir, Path.GetFileNameWithoutExtension(nameA) + ".wav");
        var wavB = Path.Combine(_dir, Path.GetFileNameWithoutExtension(nameB) + ".wav");

        WriteWav(wavA, NearCamera(world));
        WriteWav(wavB, FarCamera(world, CameraOffsetSeconds * SampleRate));

        // The far camera is also recording at a lower audio quality, which is ordinary and
        // which matters here: a perceptual codec is built to keep what the ear hears as
        // loudness and to spend its bits nowhere else, so it preserves what the coarse pass
        // measures and is free to rewrite what a 1 ms bucket measures.
        return (
            EncodeClip(nameA, wavA, CameraSeconds, "128k"),
            EncodeClip(nameB, wavB, CameraSeconds, "40k"));
    }

    /// <summary>The sound in the room, before either camera hears it.</summary>
    /// <remarks>
    /// Broadband on purpose. A single tone passes through a room almost unchanged — every
    /// reflection only shifts its phase — so two cameras would still see near enough the same
    /// waveform and this fixture would prove nothing. A spread of inharmonic partials under a
    /// wash of noise is what a room actually colours differently from two places, and that
    /// difference is the whole point of the fixture.
    /// </remarks>
    private static double[] WorldEvent()
    {
        const int seconds = CameraSeconds + CameraOffsetSeconds;
        var count = seconds * SampleRate;

        var envelope = SmoothEnvelope(seed: 20260828, samples: count);
        var random = new Random(1234);

        var world = new double[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;

            var content =
                (Math.Sin(2 * Math.PI * 190 * t) * 0.30) +
                (Math.Sin(2 * Math.PI * 437 * t) * 0.28) +
                (Math.Sin(2 * Math.PI * 921 * t) * 0.22) +
                (Math.Sin(2 * Math.PI * 1613 * t) * 0.16) +
                ((random.NextDouble() - 0.5) * 0.9);

            world[i] = envelope[i] * content * 0.7;
        }

        return world;
    }

    /// <summary>The camera in the room: close, clean, one short reflection.</summary>
    private static short[] NearCamera(double[] world)
    {
        const int reflection = SampleRate * 13 / 1000;

        var hiss = new Random(77);
        var result = new short[CameraSeconds * SampleRate];

        for (var i = 0; i < result.Length; i++)
        {
            var wet = world[i] + (i >= reflection ? world[i - reflection] * 0.25 : 0);
            result[i] = ToPcm((wet * 0.85) + ((hiss.NextDouble() - 0.5) * 0.008));
        }

        return result;
    }

    /// <summary>
    /// The camera across the room: a longer reverb, a duller microphone, its own noise floor
    /// and its own automatic gain.
    /// </summary>
    /// <remarks>
    /// Every one of those is something a normalised correlation cannot simply divide out. The
    /// reverb and the low-pass rewrite the waveform inside a bucket, which is what a fine pass
    /// compares; the automatic gain rides the level up and down over seconds, which is what a
    /// long window compares. Only the shape of the loudness in between survives both.
    /// </remarks>
    private static short[] FarCamera(double[] world, int start)
    {
        // A room, as a handful of reflections. The early cluster is what matters: arriving
        // within a few milliseconds of the direct sound, it rewrites the waveform inside a
        // 1 ms bucket while leaving the loudness over 10 ms very nearly alone. That gap is
        // the whole difference between the two passes, and it is what a second camera
        // standing across a room really does to a sound.
        var room = new (int Delay, double Gain)[]
        {
            (SampleRate * 3 / 1000, 0.55),
            (SampleRate * 5 / 1000, 0.48),
            (SampleRate * 8 / 1000, 0.40),
            (SampleRate * 11 / 1000, 0.34),
            (SampleRate * 31 / 1000, 0.26),
            (SampleRate * 57 / 1000, 0.18),
        };

        var hiss = new Random(99);
        var result = new short[CameraSeconds * SampleRate];
        var gain = SlowGain(seed: 5150, samples: result.Length);
        var low = 0.0;

        for (var i = 0; i < result.Length; i++)
        {
            var n = start + i;

            var wet = world[n];
            foreach (var (delay, level) in room)
                if (n >= delay) wet += world[n - delay] * level;

            // One pole, about 600 Hz — a duller microphone than the one in the room.
            low += 0.08 * (wet - low);

            result[i] = ToPcm((low * 0.42 * gain[i]) + ((hiss.NextDouble() - 0.5) * 0.06));
        }

        return result;
    }

    /// <summary>A camera's automatic gain: a level that wanders over seconds, not over frames.</summary>
    private static double[] SlowGain(int seed, int samples)
    {
        const int perPoint = SampleRate * 3 / 2;

        var random = new Random(seed);
        var points = new double[(samples / perPoint) + 2];
        for (var i = 0; i < points.Length; i++) points[i] = 0.65 + (random.NextDouble() * 0.7);

        var gain = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var index = i / perPoint;
            var t = (i % perPoint) / (double)perPoint;
            gain[i] = points[index] + ((points[index + 1] - points[index]) * t);
        }

        return gain;
    }

    /// <summary>
    /// Correlates decoded audio at one resolution, around an alignment already known to be
    /// right — so what it returns is how well that resolution can see a match, not whether
    /// there is one.
    /// </summary>
    private static double ConfidenceAt(
        string referencePath,
        string candidatePath,
        double referenceStart,
        double length,
        double answer,
        int rate)
    {
        using var reference = new AudioDecoder(referencePath, SampleRate);
        using var candidate = new AudioDecoder(candidatePath, SampleRate);

        var pattern = AudioPeaksBuilder.BuildRange(reference, referenceStart, length, rate);

        var searchStart = Math.Max(0, answer - 0.5);
        var signal = AudioPeaksBuilder.BuildRange(candidate, searchStart, length + 1, rate);

        var found = AudioSync.FindOffsets(new SyncRequest(pattern, 0, length, signal), take: 1);

        return found.Count == 0 ? 0 : found[0].Confidence;
    }

    /// <summary>Muxes one of the fixture's WAVs under a test pattern.</summary>
    private string EncodeClip(string name, string wav, int seconds, string audioBitrate)
    {
        var path = Path.Combine(_dir, name);

        Run(_runtime!.FfmpegPath,
        [
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", $"testsrc2=size=320x240:rate={Fps}:duration={seconds}",
            "-i", wav,
            "-c:v", "libopenh264", "-b:v", "1M", "-g", "30", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", audioBitrate,
            "-fps_mode", "cfr", "-r", Fps.ToString(),
            "-shortest",
            path,
        ]);

        return path;
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
