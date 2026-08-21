using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using BertCut.Core.Export;

namespace BertCut.Media;

/// <summary>
/// Locates the ffmpeg build BertCut runs against and reports what it can do.
/// </summary>
/// <remarks>
/// <para>
/// The probe order is deliberate: the copy shipped beside the app wins over anything on
/// PATH. A stale system ffmpeg is a real hazard — this development machine has a 2020
/// build in <c>C:\Program Files\ffmpeg</c> — and silently binding to it would produce
/// mystifying failures, because the in-process decoder is compiled against the 8.1 ABI.
/// </para>
/// <para>
/// Capabilities are feature-probed from <c>-encoders</c> and <c>-filters</c> rather than
/// inferred from the version string, since build variants differ in what they include far
/// more than versions do.
/// </para>
/// <para>
/// Those listings answer a question about the <em>build</em>, though, and what the export
/// needs to know is about the <em>machine</em>. One binary serves every user, so it names
/// NVENC, Quick Sync, AMF and <c>cuda</c> whatever card is fitted. Anything hardware-dependent
/// is therefore confirmed by use — see <see cref="ProbeHardware"/> — and dropped when it does
/// not work, so <see cref="EncoderCapabilities.SelectVideoEncoder"/> falls through to the next
/// choice instead of picking an encoder that cannot run.
/// </para>
/// </remarks>
public sealed partial class FfmpegRuntime
{
    /// <summary>
    /// The minimum avcodec major version. FFmpeg 8.1 ships avcodec 62, which is the ABI
    /// the FFmpeg.AutoGen 8.1 bindings are generated against.
    /// </summary>
    public const int MinimumAvcodecMajor = 62;

    private FfmpegRuntime(string directory, string version, EncoderCapabilities capabilities)
    {
        Directory = directory;
        Version = version;
        Capabilities = capabilities;
    }

    /// <summary>Folder holding ffmpeg.exe, ffprobe.exe, and the shared libraries.</summary>
    public string Directory { get; }

    public string FfmpegPath => Path.Combine(Directory, "ffmpeg.exe");

    public string FfprobePath => Path.Combine(Directory, "ffprobe.exe");

    /// <summary>The full version banner, for diagnostics.</summary>
    public string Version { get; }

    public EncoderCapabilities Capabilities { get; }

    /// <summary>
    /// Finds a usable ffmpeg, or throws with an actionable message.
    /// </summary>
    public static FfmpegRuntime Locate(string? explicitDirectory = null)
    {
        var tried = new List<string>();

        foreach (var candidate in CandidateDirectories(explicitDirectory))
        {
            tried.Add(candidate);

            var exe = Path.Combine(candidate, "ffmpeg.exe");
            var probe = Path.Combine(candidate, "ffprobe.exe");
            if (!File.Exists(exe) || !File.Exists(probe)) continue;

            var banner = TryRun(exe, ["-hide_banner", "-version"]);
            if (banner is null) continue;

            if (!IsRecentEnough(candidate, banner)) continue;

            var encoders = ParseNames(TryRun(exe, ["-hide_banner", "-encoders"]) ?? "");
            var filters = ParseNames(TryRun(exe, ["-hide_banner", "-filters"]) ?? "");
            var listsCuda = (TryRun(exe, ["-hide_banner", "-hwaccels"]) ?? "").Contains("cuda", StringComparison.Ordinal);

            // Anything hardware-dependent that did not prove itself comes back out of the set,
            // including the ones the probe never reached — an unprobed encoder is an unknown
            // one, and claiming it is exactly the mistake this exists to stop.
            var hardware = ProbeHardware(exe, encoders, listsCuda);
            encoders.ExceptWith(HardwareH264Encoders.Where(n => !hardware.WorkingEncoders.Contains(n)));

            var version = banner.Split('\n')[0].Trim();
            return new FfmpegRuntime(
                candidate, version, new EncoderCapabilities(encoders, filters, hardware.CudaDecode));
        }

        throw new FileNotFoundException(
            $"No usable FFmpeg {MinimumAvcodecMajor}+ build was found. Run tools/fetch-ffmpeg.ps1 to install one." +
            Environment.NewLine + "Looked in:" + Environment.NewLine +
            string.Join(Environment.NewLine, tried.Select(t => "  " + t)));
    }

    private static IEnumerable<string> CandidateDirectories(string? explicitDirectory)
    {
        if (explicitDirectory is not null) yield return explicitDirectory;

        // Beside the app, which is where the build copies tools/ffmpeg.
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        yield return AppContext.BaseDirectory;

        // The repo copy, so tests and `dotnet run` work from a source tree.
        var repo = FindRepoRoot(AppContext.BaseDirectory);
        if (repo is not null) yield return Path.Combine(repo, "tools", "ffmpeg");

        // A copy the user installed themselves. In the profile rather than under LocalAppData,
        // because %LOCALAPPDATA%\BertCut is the Velopack install directory and the installer
        // empties it — an ffmpeg put there would survive exactly until the next update.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bertcut", "ffmpeg");

        // The location that used to be, still honoured for anyone who put one there.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BertCut", "ffmpeg");

        // PATH last, and still subject to the version check below.
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(entry))
                yield return entry.Trim();
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Rejects builds older than the ABI the in-process decoder expects.
    /// </summary>
    private static bool IsRecentEnough(string directory, string banner)
    {
        // Prefer the shared libraries' own major version when present — it is the number
        // that actually has to match the bindings.
        var avcodec = System.IO.Directory.Exists(directory)
            ? System.IO.Directory.EnumerateFiles(directory, "avcodec-*.dll").FirstOrDefault()
            : null;

        if (avcodec is not null)
        {
            var match = AvcodecVersion().Match(Path.GetFileName(avcodec));
            if (match.Success && int.TryParse(match.Groups[1].Value, out var major))
                return major >= MinimumAvcodecMajor;
        }

        // Static builds have no DLLs; fall back to the reported release.
        var version = ReleaseVersion().Match(banner);
        return version.Success
            && int.TryParse(version.Groups[1].Value, out var release)
            && release >= 8;
    }

    /// <summary>
    /// Extracts codec/filter names from the tabular <c>-encoders</c> / <c>-filters</c>
    /// output, where each entry is a flags column followed by the name.
    /// </summary>
    private static HashSet<string> ParseNames(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n'))
        {
            var match = TableRow().Match(line);
            if (match.Success) names.Add(match.Groups[1].Value);
        }

        return names;
    }

    private static string? TryRun(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(20_000);

            // -version writes to stdout, but some builds report parts on stderr.
            return stdout.Length > 0 ? stdout : stderr;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The H.264 encoders that need a particular piece of hardware behind them, in the order
    /// <see cref="EncoderCapabilities.SelectVideoEncoder"/> prefers them.
    /// </summary>
    private static readonly string[] HardwareH264Encoders = ["h264_nvenc", "h264_qsv", "h264_amf"];

    /// <summary>
    /// Keyed by ffmpeg path, because the answer is a property of this machine and cannot change
    /// while the process runs. Without it every <see cref="Locate"/> — one per test fixture —
    /// would pay for the probe again.
    /// </summary>
    private static readonly ConcurrentDictionary<string, HardwareProbe> ProbeCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What this machine's hardware will actually do, as opposed to what ffmpeg lists.</summary>
    private sealed record HardwareProbe(IReadOnlySet<string> WorkingEncoders, bool CudaDecode);

    /// <summary>
    /// Narrows the listed capabilities to the ones that work here, by using them.
    /// </summary>
    /// <remarks>
    /// This is the difference between a build's features and a machine's. BtbN ships one binary
    /// for everyone, so <c>-encoders</c> names <c>h264_nvenc</c>, <c>h264_qsv</c> and
    /// <c>h264_amf</c>, and <c>-hwaccels</c> names <c>cuda</c>, on a machine with none of them —
    /// those listings report what was compiled in and nothing else. Selecting from them directly
    /// picked NVENC everywhere and exported successfully only on a machine that happened to have
    /// an NVIDIA card; anywhere else every export died at <c>Cannot load nvcuda.dll</c>, and the
    /// GitHub runner was the first machine without one to try it.
    /// </remarks>
    private static HardwareProbe ProbeHardware(string exe, IReadOnlySet<string> listed, bool listsCuda) =>
        ProbeCache.GetOrAdd(exe, _ =>
        {
            var working = new HashSet<string>(StringComparer.Ordinal);

            // Preference order, and it stops at the first that works: SelectVideoEncoder would
            // never reach the ones behind it, so probing them is startup latency spent on an
            // answer nothing reads. Each probe launches ffmpeg — around 300 ms for one that
            // succeeds, under 150 ms for one that fails at device initialisation.
            foreach (var encoder in HardwareH264Encoders)
            {
                if (!listed.Contains(encoder) || !EncodesAFrame(exe, encoder)) continue;

                working.Add(encoder);
                break;
            }

            return new HardwareProbe(working, listsCuda && CudaDeviceOpens(exe));
        });

    /// <summary>
    /// Whether <paramref name="encoder"/> can encode a frame here. 256x256 clears every
    /// hardware encoder's minimum dimensions; the failures are the fast case, since a missing
    /// device fails at initialisation rather than after any work.
    /// </summary>
    private static bool EncodesAFrame(string exe, string encoder) => RunSucceeds(exe,
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-f", "lavfi", "-i", "color=c=black:s=256x256:r=25:d=0.04",
        "-c:v", encoder, "-frames:v", "1", "-f", "null", "-",
    ]);

    /// <summary>
    /// Whether a CUDA device opens here — which is what <c>-hwaccel cuda</c> needs and what
    /// <c>-hwaccels</c> listing it does not promise.
    /// </summary>
    private static bool CudaDeviceOpens(string exe) => RunSucceeds(exe,
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-init_hw_device", "cuda",
        "-f", "lavfi", "-i", "color=c=black:s=64x64:r=25:d=0.04",
        "-frames:v", "1", "-f", "null", "-",
    ]);

    /// <summary>Runs ffmpeg and reports whether it exited cleanly, discarding its output.</summary>
    private static bool RunSucceeds(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return false;

            // Drained before the wait: -loglevel error keeps this to a line or two, but a full
            // pipe would block the child rather than the probe returning false.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^avcodec-(\d+)\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex AvcodecVersion();

    [GeneratedRegex(@"ffmpeg version n?(\d+)\.")]
    private static partial Regex ReleaseVersion();

    /// <summary>
    /// Matches a row of the <c>-encoders</c> / <c>-filters</c> tables: a flags column of
    /// dots and capitals, then the name.
    /// </summary>
    /// <remarks>
    /// The two tables do not agree on the flags column width — <c>-encoders</c> uses six
    /// characters (<c>V....D</c>) while <c>-filters</c> in FFmpeg 8.x uses two
    /// (<c>TS</c>, <c>..</c>), having been three in older builds. The range covers all of
    /// them. The legend lines above each table (<c>T.. = Timeline support</c>, <c>A =
    /// Audio input/output</c>) do not match, because the token after the flags must be a
    /// bare identifier rather than <c>=</c>.
    /// </remarks>
    [GeneratedRegex(@"^\s*[A-Z\.]{2,6}\s+([A-Za-z0-9_]+)\s+")]
    private static partial Regex TableRow();
}
