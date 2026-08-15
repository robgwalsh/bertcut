using System.Diagnostics;
using BertCut.Core.Audio;
using BertCut.Core.Session;
using BertCut.Media;
using BertCut.Media.Audio;
using BertCut.Media.Decode;

namespace BertCut.Core.Tests;

/// <summary>
/// The audio decoder and the envelope it produces, against real files.
/// </summary>
/// <remarks>
/// <see cref="AudioSyncTests"/> proves the correlation against planted offsets with no
/// ffmpeg at all. These prove the other half: that what comes out of a real file is the
/// envelope the correlation assumes it is getting, at the right rate and the right position.
/// </remarks>
[Collection("ffmpeg")]
public class AudioPeaksTests : IDisposable
{
    private readonly FfmpegRuntime? _runtime;
    private readonly string _dir;
    private readonly string? _previousStateDir;

    public AudioPeaksTests()
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

        // The cache lives under the state root, so point that at scratch — otherwise a run
        // would write envelopes into the user's own cache.
        _previousStateDir = Environment.GetEnvironmentVariable(AppPaths.OverrideVariable);
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, Path.Combine(_dir, "state"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, _previousStateDir);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Six seconds of silence with a 440 Hz tone burst between two and four seconds.
    /// </summary>
    /// <remarks>
    /// A gated tone rather than a continuous one, so there is an unambiguous "where is the
    /// sound" the peaks can be checked against by position, not just by level.
    /// </remarks>
    private string MakeBurst(string name)
    {
        var path = Path.Combine(_dir, name);

        Run(_runtime!.FfmpegPath,
        [
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", "aevalsrc=0.8*sin(440*2*PI*t)*between(t\\,2\\,4):d=6:s=48000",
            "-c:a", "aac", "-b:a", "128k",
            path,
        ]);

        return path;
    }

    [SkippableFact]
    public void Peaks_land_where_the_sound_is()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        using var decoder = new AudioDecoder(MakeBurst("burst.m4a"), sampleRate: 48000);
        var peaks = AudioPeaksBuilder.Build(decoder);

        Assert.Equal(AudioPeaks.DefaultRate, peaks.Rate);
        Assert.InRange(peaks.DurationSeconds, 5.5, 6.5);

        // Loud inside the burst, quiet either side. AAC rings a little at the edges, so the
        // samples are taken well inside each region rather than on the boundaries.
        Assert.True(peaks.EnvelopeAt(peaks.BucketOf(3.0)) > 1.0,
            $"the burst should be loud, was {peaks.EnvelopeAt(peaks.BucketOf(3.0)):0.000}");

        Assert.True(peaks.EnvelopeAt(peaks.BucketOf(1.0)) < 0.1,
            $"before the burst should be quiet, was {peaks.EnvelopeAt(peaks.BucketOf(1.0)):0.000}");

        Assert.True(peaks.EnvelopeAt(peaks.BucketOf(5.0)) < 0.1,
            $"after the burst should be quiet, was {peaks.EnvelopeAt(peaks.BucketOf(5.0)):0.000}");
    }

    [SkippableFact]
    public void The_decoder_resamples_to_the_rate_and_channel_count_it_was_asked_for()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        // The file is 48 kHz; ask for 44.1 kHz mono and count what comes out.
        using var decoder = new AudioDecoder(MakeBurst("rate.m4a"), sampleRate: 44100, channels: 1);

        Assert.Equal(44100, decoder.SampleRate);
        Assert.Equal(1, decoder.Channels);

        var buffer = new float[4096];
        long frames = 0;

        while (true)
        {
            var read = decoder.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            frames += read;
        }

        // Six seconds at 44.1 kHz, allowing for the encoder's priming samples.
        Assert.InRange(frames, 44100 * 5.5, 44100 * 6.5);
    }

    [SkippableFact]
    public void Seeking_lands_on_the_second_it_was_given()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        using var decoder = new AudioDecoder(MakeBurst("seek.m4a"), sampleRate: 48000);

        Assert.True(Loudness(decoder, at: 3.0) > 0.3, "inside the burst should be loud");
        Assert.True(Loudness(decoder, at: 0.5) < 0.05, "before the burst should be quiet");

        // Seeking backwards then forwards again exercises the flush, which is where a decoder
        // most often starts returning stale samples.
        Assert.True(Loudness(decoder, at: 3.5) > 0.3, "inside the burst again should be loud");
        Assert.True(Loudness(decoder, at: 5.0) < 0.05, "after the burst should be quiet");
    }

    [SkippableFact]
    public void A_built_envelope_is_cached_and_reloaded_unchanged()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var path = MakeBurst("cached.m4a");
        const string key = "test-content-key";

        var built = AudioPeaksCache.GetOrBuild(path, key, sampleRate: 48000);
        Assert.NotNull(built);
        Assert.True(AudioPeaksCache.IsBuilt(key));

        var loaded = AudioPeaksCache.TryLoad(key);
        Assert.NotNull(loaded);

        Assert.Equal(built!.Rate, loaded!.Rate);
        Assert.Equal(built.Min, loaded.Min);
        Assert.Equal(built.Max, loaded.Max);
    }

    [SkippableFact]
    public void A_file_with_no_audio_yields_no_envelope_rather_than_throwing()
    {
        Skip.If(_runtime is null, "No FFmpeg 8+ build found.");

        var silent = Path.Combine(_dir, "silent.mp4");
        Run(_runtime!.FfmpegPath,
        [
            "-hide_banner", "-y", "-nostdin",
            "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=30:duration=1",
            "-c:v", "libopenh264", "-b:v", "500k", "-pix_fmt", "yuv420p",
            silent,
        ]);

        Assert.False(AudioDecoder.HasAudioStream(silent));
        Assert.Null(AudioPeaksCache.GetOrBuild(silent, "silent-key", sampleRate: 48000));
    }

    [Fact]
    public void A_truncated_cache_file_is_ignored_rather_than_loaded()
    {
        const string key = "truncated-key";

        AudioPeaksCache.Save(key, new AudioPeaks(100, new float[50], new float[50]));

        var path = AudioPeaksCache.PathFor(key);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length - 8)]);

        Assert.Null(AudioPeaksCache.TryLoad(key));
    }

    /// <summary>Mean absolute level of a tenth of a second starting at <paramref name="at"/>.</summary>
    private static double Loudness(AudioDecoder decoder, double at)
    {
        decoder.SeekTo(at);

        var frames = decoder.SampleRate / 10;
        var buffer = new float[frames * decoder.Channels];
        var read = decoder.Read(buffer, 0, frames);

        if (read == 0) return 0;

        double total = 0;
        var samples = read * decoder.Channels;
        for (var i = 0; i < samples; i++) total += Math.Abs(buffer[i]);

        return total / samples;
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
