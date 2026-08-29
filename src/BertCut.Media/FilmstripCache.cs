using System.Diagnostics;
using System.Globalization;
using BertCut.Core.Media;
using BertCut.Core.Session;

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

    private static string Root => AppPaths.Cache;

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
    /// Deletes the thumbnails this class used to write, leaving everything else alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing reads or writes a filmstrip any more — the timeline draws segment fills and a
    /// waveform instead — but earlier versions could have left gigabytes of JPEGs behind, so
    /// the reclaim still has to happen once on each machine.
    /// </para>
    /// <para>
    /// <b>It removes files, never directories.</b> This shares a cache root with
    /// <see cref="Audio.AudioPeaksCache"/>, and the two write into the <i>same</i>
    /// <c>cache/&lt;contentKey&gt;</c> directory — so a whole-directory delete here takes the audio
    /// envelope with it. It did: this ran at every startup with a zero budget, which made every
    /// directory over budget, so <c>peaks.bin</c> was deleted on every launch and the envelope
    /// was rebuilt by a full audio decode on the first open after each one. Nothing said so,
    /// because the cost landed on the next thing the user did rather than here.
    /// </para>
    /// </remarks>
    public static void PurgeThumbnails()
    {
        if (!Directory.Exists(Root)) return;

        foreach (var directory in new DirectoryInfo(Root).EnumerateDirectories())
        {
            try
            {
                foreach (var file in directory.EnumerateFiles("t*.jpg")) file.Delete();

                var marker = Path.Combine(directory.FullName, ".complete");
                if (File.Exists(marker)) File.Delete(marker);

                // Only when this class was the sole occupant. A directory that still holds an
                // envelope is not ours to remove.
                if (!directory.EnumerateFileSystemInfos().Any()) directory.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* in use */ }
        }
    }
}
