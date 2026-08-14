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
            var hasCuda = (TryRun(exe, ["-hide_banner", "-hwaccels"]) ?? "").Contains("cuda", StringComparison.Ordinal);

            var version = banner.Split('\n')[0].Trim();
            return new FfmpegRuntime(candidate, version, new EncoderCapabilities(encoders, filters, hasCuda));
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
