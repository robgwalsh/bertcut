using BertCut.Core.Session;
using BertCut.Media;
using BertCut.Media.Audio;

namespace BertCut.Core.Tests;

/// <summary>
/// The two caches share a directory per content key, so neither may delete one wholesale.
/// </summary>
/// <remarks>
/// The first test here is a regression. <c>FilmstripCache.Trim(budgetBytes: 0)</c> ran on every
/// startup and deleted each <c>cache/&lt;contentKey&gt;</c> directory recursively, which took
/// <c>peaks.bin</c> with it — so the audio envelope never survived a restart and the first open
/// after every launch paid a full audio decode to rebuild it. Nothing pointed at the cause,
/// because the cost landed on the next thing the user did.
/// </remarks>
// BERTCUT_STATE_DIR is process-wide; this shares the collection that serialises the others.
[Collection("ffmpeg")]
public class CacheTrimTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "bertcut-cachetrim-" + Guid.NewGuid().ToString("n"));

    private readonly string? _previousOverride =
        Environment.GetEnvironmentVariable(AppPaths.OverrideVariable);

    public CacheTrimTests() =>
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, _temp);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, _previousOverride);
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Purging_thumbnails_leaves_the_audio_envelope()
    {
        var peaks = WriteEntry("aaaa", thumbnails: 3, peaksBytes: 2048);

        FilmstripCache.PurgeThumbnails();

        Assert.True(File.Exists(peaks), "the envelope was deleted by a thumbnail purge");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(peaks)!, "t*.jpg"));
    }

    [Fact]
    public void Purging_thumbnails_removes_a_directory_it_emptied()
    {
        var directory = Path.Combine(AppPaths.Cache, "bbbb");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "t000001.jpg"), new byte[16]);
        File.WriteAllBytes(Path.Combine(directory, ".complete"), []);

        FilmstripCache.PurgeThumbnails();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Trimming_envelopes_keeps_the_most_recently_used()
    {
        var older = WriteEntry("cccc", thumbnails: 0, peaksBytes: 4096);
        var newer = WriteEntry("dddd", thumbnails: 0, peaksBytes: 4096);

        File.SetLastAccessTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        File.SetLastAccessTimeUtc(newer, DateTime.UtcNow);

        // A budget that fits exactly one of them.
        AudioPeaksCache.Trim(budgetBytes: 4096);

        Assert.True(File.Exists(newer), "the most recently used envelope was evicted");
        Assert.False(File.Exists(older), "the least recently used envelope was kept");
    }

    [Fact]
    public void Trimming_envelopes_keeps_everything_inside_the_budget()
    {
        var kept = WriteEntry("eeee", thumbnails: 0, peaksBytes: 1024);

        AudioPeaksCache.Trim(budgetBytes: 512L * 1024 * 1024);

        Assert.True(File.Exists(kept));
    }

    /// <summary>Writes one cache directory holding an envelope and some thumbnails.</summary>
    private static string WriteEntry(string contentKey, int thumbnails, int peaksBytes)
    {
        var directory = Path.Combine(AppPaths.Cache, contentKey);
        Directory.CreateDirectory(directory);

        for (var i = 0; i < thumbnails; i++)
            File.WriteAllBytes(Path.Combine(directory, $"t{i:D6}.jpg"), new byte[64]);

        var peaks = AudioPeaksCache.PathFor(contentKey);
        File.WriteAllBytes(peaks, new byte[peaksBytes]);

        return peaks;
    }
}
