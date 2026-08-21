using System.Diagnostics;
using BertCut.Core.Export;
using BertCut.Media;

namespace BertCut.Core.Tests;

/// <summary>
/// Holds the reported capabilities to what the machine will actually do.
/// </summary>
/// <remarks>
/// These pass on a machine with an NVIDIA card and on one with no GPU at all, which is the
/// point of them: the bug they exist for reported NVENC and CUDA everywhere, because
/// <c>-encoders</c> and <c>-hwaccels</c> describe the build rather than the hardware. It
/// exported fine on the development machine and failed on every other one, and nothing in
/// the suite noticed until CI ran on a runner without a card.
/// </remarks>
[Collection("ffmpeg")]
public class FfmpegRuntimeTests
{
    private readonly FfmpegRuntime? _runtime;

    public FfmpegRuntimeTests()
    {
        try
        {
            _runtime = FfmpegRuntime.Locate();
        }
        catch (FileNotFoundException)
        {
            _runtime = null;
        }
    }

    /// <summary>
    /// Every H.264 encoder reported must encode a frame. This is the assertion that would
    /// have failed on the GitHub runner before the probe existed.
    /// </summary>
    [SkippableFact]
    public void Every_reported_h264_encoder_can_encode_a_frame()
    {
        Skip.If(_runtime is null, "No ffmpeg installed.");

        string[] candidates = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264", "libopenh264"];

        foreach (var encoder in candidates.Where(_runtime!.Capabilities.Has))
        {
            var exit = Run(_runtime.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "color=c=black:s=256x256:r=25:d=0.04",
                "-c:v", encoder, "-frames:v", "1", "-f", "null", "-",
            ]);

            Assert.True(exit == 0, $"{encoder} is reported as available but exited {exit}.");
        }
    }

    /// <summary>
    /// An encoder is always left to fall back to, whatever the hardware turns out to be —
    /// otherwise narrowing the set would swap a broken export for no export at all.
    /// </summary>
    [SkippableFact]
    public void Some_h264_encoder_always_survives_the_probe()
    {
        Skip.If(_runtime is null, "No ffmpeg installed.");

        // Throws if nothing is usable, which is the failure this is guarding against.
        var encoder = _runtime!.Capabilities.SelectVideoEncoder();

        Assert.True(_runtime.Capabilities.Has(EncoderCapabilities.NameOf(encoder)));
    }

    /// <summary>
    /// <c>-hwaccel cuda</c> is only asked for when a CUDA device actually opens. Reporting it
    /// off a listing is what put <c>-hwaccel cuda</c> on every export and killed all three
    /// end-to-end tests on a runner with no card.
    /// </summary>
    [SkippableFact]
    public void Cuda_is_only_reported_when_a_device_opens()
    {
        Skip.If(_runtime is null, "No ffmpeg installed.");
        Skip.IfNot(_runtime!.Capabilities.HasCudaHwaccel, "No CUDA device on this machine.");

        var exit = Run(_runtime.FfmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-init_hw_device", "cuda",
            "-f", "lavfi", "-i", "color=c=black:s=64x64:r=25:d=0.04",
            "-frames:v", "1", "-f", "null", "-",
        ]);

        Assert.Equal(0, exit);
    }

    private static int Run(string exe, string[] args)
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
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode;
    }
}
