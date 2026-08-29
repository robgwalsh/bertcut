using BertCut.Core.Export;
using BertCut.Core.Session;
using BertCut.Media;

namespace BertCut.Core.Tests;

/// <summary>
/// The memo that keeps ffmpeg off the path between the process starting and the window existing.
/// </summary>
/// <remarks>
/// Locating ffmpeg used to launch five to eight child processes on the UI thread before
/// <c>MainWindow</c> was constructed, each mapping ~139 MB of shared libraries and each measured
/// in <c>FfmpegRuntime</c> at 150-300 ms. None of it changes between launches on a machine nobody
/// has touched. These count launches rather than timing them, for the reason
/// <c>VideoDecoder.SeekCount</c> gives: the cost is real, but a stopwatch in a test fails on a
/// busy machine.
/// </remarks>
[Collection("ffmpeg")]
public class FfmpegCapabilityCacheTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "bertcut-caps-" + Guid.NewGuid().ToString("n"));

    private readonly string? _previousOverride =
        Environment.GetEnvironmentVariable(AppPaths.OverrideVariable);

    public FfmpegCapabilityCacheTests() =>
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, _temp);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, _previousOverride);
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_saved_answer_comes_back_for_the_same_binary()
    {
        var exe = FakeExe();
        var capabilities = new EncoderCapabilities(
            new HashSet<string> { "h264_nvenc", "aac" },
            new HashSet<string> { "crop", "overlay" },
            HasCudaHwaccel: true);

        FfmpegCapabilityCache.Save(exe, "ffmpeg version 8.1", capabilities);

        var loaded = FfmpegCapabilityCache.Load(exe);

        Assert.NotNull(loaded);
        Assert.Equal("ffmpeg version 8.1", loaded!.Value.Version);
        Assert.True(loaded.Value.Capabilities.Has("h264_nvenc"));
        Assert.True(loaded.Value.Capabilities.HasCudaHwaccel);
        Assert.Contains("overlay", loaded.Value.Capabilities.Filters);
    }

    /// <summary>
    /// An update replaces the binaries, and an old answer must not describe a new build.
    /// </summary>
    [Fact]
    public void A_changed_binary_is_not_described_by_the_old_answer()
    {
        var exe = FakeExe();
        FfmpegCapabilityCache.Save(exe, "old", EncoderCapabilities.NvencOnly);

        // Same path, different contents and timestamp — which is what an update looks like.
        File.WriteAllBytes(exe, new byte[4096]);
        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(5));

        Assert.Null(FfmpegCapabilityCache.Load(exe));
    }

    [Fact]
    public void Invalidating_forgets_the_answer()
    {
        var exe = FakeExe();
        FfmpegCapabilityCache.Save(exe, "v", EncoderCapabilities.NvencOnly);
        Assert.NotNull(FfmpegCapabilityCache.Load(exe));

        FfmpegCapabilityCache.Invalidate();

        Assert.Null(FfmpegCapabilityCache.Load(exe));
    }

    /// <summary>
    /// The rule the whole thing exists for: the second launch launches nothing.
    /// </summary>
    [SkippableFact]
    public void A_second_locate_launches_no_processes()
    {
        FfmpegRuntime cold;
        try { cold = FfmpegRuntime.Locate(); }
        catch (FileNotFoundException) { throw new SkipException("No ffmpeg installed."); }

        // The first call populated the cache; count only what a subsequent one costs.
        var before = FfmpegRuntime.ProbeLaunches;
        var warm = FfmpegRuntime.Locate();
        var launched = FfmpegRuntime.ProbeLaunches - before;

        Assert.Equal(0, launched);
        Assert.Equal(cold.Version, warm.Version);
        Assert.Equal(cold.Directory, warm.Directory);
        Assert.Equal(
            cold.Capabilities.SelectVideoEncoder(), warm.Capabilities.SelectVideoEncoder());
    }

    /// <summary>A stand-in binary: the fingerprint reads its length and write time, never its contents.</summary>
    private string FakeExe()
    {
        Directory.CreateDirectory(_temp);
        var exe = Path.Combine(_temp, "ffmpeg.exe");
        File.WriteAllBytes(exe, new byte[64]);
        return exe;
    }
}
