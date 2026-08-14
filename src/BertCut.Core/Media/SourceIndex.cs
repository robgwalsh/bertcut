using BertCut.Core.Time;

namespace BertCut.Core.Media;

/// <summary>
/// The exact presentation timestamp of every video frame in a source, plus which of them
/// are keyframes.
/// </summary>
/// <remarks>
/// <para>
/// This exists because screen recorders lie about frame rate. OBS, ShareX, Camtasia and
/// the Windows capture tools all routinely emit variable-frame-rate MP4, where a frame's
/// timestamp is <em>not</em> <c>index / fps</c>. Computing timestamps from a nominal rate
/// under VFR puts cuts in the wrong place and drifts audio — and it surfaces as an
/// apparent bug in ripple delete, which is nowhere near the actual cause.
/// </para>
/// <para>
/// With this table every frame-to-timestamp conversion is an array index and every
/// timestamp-to-frame conversion is a binary search, so variable frame rate stops being a
/// concept anywhere above the decoder. It also makes seek cost computable rather than
/// guessed (the distance back to the preceding keyframe), which is what the playback
/// pre-roll window is derived from, and it makes the lossless-export keyframe check O(1).
/// </para>
/// <para>
/// Built once per source by a non-decoding ffprobe pass and cached on disk. About 864 KB
/// per hour at 30 fps.
/// </para>
/// </remarks>
public sealed class SourceIndex
{
    public SourceIndex(Rational timeBase, long[] pts, int[] keyFrames)
    {
        TimeBase = timeBase;
        Pts = pts;
        KeyFrames = keyFrames;
    }

    /// <summary>The video stream's time base, in seconds per tick.</summary>
    public Rational TimeBase { get; }

    /// <summary>Presentation timestamp of each frame, in <see cref="TimeBase"/> ticks, ascending.</summary>
    public long[] Pts { get; }

    /// <summary>Indices into <see cref="Pts"/> that are keyframes, ascending.</summary>
    public int[] KeyFrames { get; }

    public int FrameCount => Pts.Length;

    /// <summary>Timestamp of a frame, in <see cref="TimeBase"/> ticks.</summary>
    public long PtsOf(long frame) => Pts[frame];

    /// <summary>Timestamp of a frame, in seconds — for ffmpeg's <c>-ss</c>/<c>-to</c>.</summary>
    public double SecondsOf(long frame) => Pts[frame] * TimeBase.Approx;

    /// <summary>
    /// The frame whose timestamp is <paramref name="pts"/>, or the nearest earlier frame.
    /// </summary>
    public long FrameOf(long pts)
    {
        var i = Array.BinarySearch(Pts, pts);
        if (i >= 0) return i;

        var insert = ~i;
        return Math.Max(0, insert - 1);
    }

    /// <summary>True when <paramref name="frame"/> can start a decode without a preceding one.</summary>
    public bool IsKeyFrame(long frame) => Array.BinarySearch(KeyFrames, (int)frame) >= 0;

    /// <summary>
    /// The last keyframe at or before <paramref name="frame"/> — where an exact seek must
    /// begin decoding.
    /// </summary>
    public long KeyFrameAtOrBefore(long frame)
    {
        if (KeyFrames.Length == 0) return 0;

        var i = Array.BinarySearch(KeyFrames, (int)frame);
        if (i >= 0) return KeyFrames[i];

        var insert = ~i;
        return insert == 0 ? KeyFrames[0] : KeyFrames[insert - 1];
    }

    /// <summary>The first keyframe at or after <paramref name="frame"/>, or -1 if none.</summary>
    public long KeyFrameAtOrAfter(long frame)
    {
        if (KeyFrames.Length == 0) return -1;

        var i = Array.BinarySearch(KeyFrames, (int)frame);
        if (i >= 0) return KeyFrames[i];

        var insert = ~i;
        return insert < KeyFrames.Length ? KeyFrames[insert] : -1;
    }

    /// <summary>
    /// How many frames must be decoded and thrown away to land exactly on
    /// <paramref name="frame"/>. Drives the playback pre-roll window.
    /// </summary>
    public long DecodeDistanceToExact(long frame) => frame - KeyFrameAtOrBefore(frame);

    /// <summary>
    /// Detects variable frame rate by comparing inter-frame gaps.
    /// </summary>
    /// <remarks>
    /// Reported to the user as a badge, and used to decide whether the fast
    /// same-rate path in the timeline resolver is safe to trust for display purposes.
    /// </remarks>
    public bool LooksVariableRate()
    {
        if (Pts.Length < 3) return false;

        var first = Pts[1] - Pts[0];
        if (first <= 0) return true;

        for (var i = 2; i < Pts.Length; i++)
        {
            var gap = Pts[i] - Pts[i - 1];

            // A one-tick wobble is normal rounding in a 1/90000 time base; anything
            // materially different is a genuinely variable rate.
            if (Math.Abs(gap - first) > 1) return true;
        }

        return false;
    }

    /// <summary>
    /// Gaps far larger than nominal, which mean the recorder stalled and dropped frames.
    /// </summary>
    /// <remarks>
    /// Worth warning about at import: the recording itself has missing time, and no edit
    /// decision can recover it.
    /// </remarks>
    public IReadOnlyList<long> FindDroppedFrameGaps(double threshold = 3.0)
    {
        if (Pts.Length < 3) return [];

        var gaps = new long[Pts.Length - 1];
        for (var i = 1; i < Pts.Length; i++) gaps[i - 1] = Pts[i] - Pts[i - 1];

        var sorted = (long[])gaps.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        if (median <= 0) return [];

        var result = new List<long>();
        for (var i = 0; i < gaps.Length; i++)
            if (gaps[i] > median * threshold)
                result.Add(i);

        return result;
    }
}
