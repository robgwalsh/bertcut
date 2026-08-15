using System.Diagnostics;
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
/// </remarks>
internal static class SampleMedia
{
    public static void Write(FfmpegRuntime runtime, string path, int seconds)
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
            "-f", "lavfi", "-i", $"testsrc2=size=640x360:rate=30:duration={seconds}",
            "-c:v", "libopenh264", "-b:v", "4M", "-g", "30", "-pix_fmt", "yuv420p",
            "-fps_mode", "cfr", "-r", "30",
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
    }
}
