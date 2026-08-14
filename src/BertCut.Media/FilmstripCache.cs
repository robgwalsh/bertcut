using System.Diagnostics;
using System.Globalization;
using BertCut.Core.Media;

namespace BertCut.Media;

/// <summary>
/// Builds and serves the low-resolution thumbnails the timeline draws and scrubbing shows.
/// </summary>
/// <remarks>
/// <para>
/// Two tiers make scrubbing feel free. While the playhead is being dragged the preview
/// shows the nearest cached thumbnail, which costs a file read and no decode; when the
/// drag settles, an exact decode replaces it. Without the first tier every scrub tick
/// would pay a seek-flush-and-decode, which at a long GOP is tens of milliseconds and
/// reads as lag.
/// </para>
/// <para>
/// <b>The cache is keyed in source space, never timeline space.</b> A ripple delete
/// changes which source frame a timeline position maps to, but not the frames themselves,
/// so cutting never invalidates a single thumbnail — only the mapping is recomputed. This
/// is the whole reason the cache directory is named by content key rather than by project.
/// </para>
/// </remarks>
public sealed class FilmstripCache(FfmpegRuntime runtime)
{
    /// <summary>Thumbnails per second of source.</summary>
    public const int ThumbnailsPerSecond = 4;

    /// <summary>Thumbnail width in pixels; height follows the source aspect.</summary>
    public const int ThumbnailWidth = 160;

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BertCut", "cache");

    public static string DirectoryFor(string contentKey) => Path.Combine(Root, contentKey);

    /// <summary>True when a complete filmstrip already exists for this source.</summary>
    public static bool IsBuilt(string contentKey) =>
        File.Exists(Path.Combine(DirectoryFor(contentKey), ".complete"));

    /// <summary>
    /// Generates thumbnails for a source, unless they already exist.
    /// </summary>
    /// <remarks>
    /// A completion marker is written last, so a run interrupted by a crash or a cancel
    /// leaves the cache visibly incomplete and is simply rebuilt next time rather than
    /// serving a half-populated strip.
    /// </remarks>
    public async Task BuildAsync(
        string path,
        string contentKey,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = DirectoryFor(contentKey);
        if (IsBuilt(contentKey)) return;

        Directory.CreateDirectory(directory);

        var psi = new ProcessStartInfo(runtime.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in new[]
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", path,
            "-vf", $"fps={ThumbnailsPerSecond},scale={ThumbnailWidth}:-2",
            "-q:v", "6",
            "-progress", "pipe:1", "-nostats", "-loglevel", "error",
            Path.Combine(directory, "t%06d.jpg"),
        })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for the filmstrip.");

        var drainError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (progress is null || !line.StartsWith("frame=", StringComparison.Ordinal)) continue;
                if (long.TryParse(line.AsSpan(6), out var frames)) progress.Report(frames);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        await drainError.ConfigureAwait(false);

        if (process.ExitCode == 0)
            await File.WriteAllTextAsync(Path.Combine(directory, ".complete"), "1", CancellationToken.None)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// The thumbnail file covering <paramref name="sourceFrame"/>, or null when absent.
    /// </summary>
    public static string? ThumbnailFor(string contentKey, long sourceFrame, SourceIndex index)
    {
        var seconds = index.SecondsOf(Math.Clamp(sourceFrame, 0, index.FrameCount - 1));

        // ffmpeg numbers the files from 1.
        var ordinal = (long)(seconds * ThumbnailsPerSecond) + 1;

        var path = Path.Combine(
            DirectoryFor(contentKey),
            $"t{ordinal.ToString("D6", CultureInfo.InvariantCulture)}.jpg");

        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Trims the cache to a size budget, oldest first.
    /// </summary>
    /// <remarks>
    /// Run on a background thread at startup. Deleting a filmstrip is always safe — it
    /// costs a rebuild, never data.
    /// </remarks>
    public static void Trim(long budgetBytes = 4L * 1024 * 1024 * 1024)
    {
        if (!Directory.Exists(Root)) return;

        var directories = new DirectoryInfo(Root)
            .EnumerateDirectories()
            .Select(d => (Directory: d, Size: d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)))
            .OrderByDescending(d => d.Directory.LastWriteTimeUtc)
            .ToList();

        long running = 0;
        foreach (var (directory, size) in directories)
        {
            running += size;
            if (running <= budgetBytes) continue;

            try { directory.Delete(recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* in use */ }
        }
    }
}
