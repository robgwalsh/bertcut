using System.Globalization;

namespace BertCut.Core.Export;

/// <summary>One completed progress block from ffmpeg's <c>-progress</c> stream.</summary>
public readonly record struct ProgressUpdate(
    long OutTimeMicroseconds,
    long Frame,
    double? Fps,
    double? SpeedMultiple,
    long? TotalSizeBytes,
    bool IsFinal)
{
    public TimeSpan OutTime => TimeSpan.FromTicks(OutTimeMicroseconds * 10);
}

/// <summary>
/// Parses the key=value blocks ffmpeg writes to stdout under <c>-progress pipe:1</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each block ends with a <c>progress=continue</c> or <c>progress=end</c> line, so lines
/// are accumulated until one of those arrives.
/// </para>
/// <para>
/// <b>The trap this class exists to contain:</b> <c>out_time_ms</c> is misnamed. ffmpeg
/// prints the same microsecond value for both <c>out_time_us</c> and <c>out_time_ms</c>
/// (see <c>fftools/ffmpeg.c</c>), and has for over a decade. Treating it as milliseconds
/// makes every progress bar read 1000x too fast. Both keys are parsed here as
/// microseconds.
/// </para>
/// <para>
/// Every numeric field can also be the literal string <c>N/A</c>, which appears at the
/// start of every encode before the first frame is written.
/// </para>
/// </remarks>
public sealed class ProgressParser
{
    private long _outTimeUs;
    private long _frame;
    private double? _fps;
    private double? _speed;
    private long? _totalSize;

    /// <summary>
    /// Feeds one line. Returns an update when the line completed a block, else null.
    /// </summary>
    public ProgressUpdate? Feed(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return null;

        var key = line.AsSpan(0, eq).Trim();
        var value = line.AsSpan(eq + 1).Trim();

        switch (key)
        {
            // Both keys carry microseconds despite the name. Do not "fix" this to /1000.
            case "out_time_us":
            case "out_time_ms":
                if (TryLong(value, out var us)) _outTimeUs = us;
                return null;

            case "out_time":
                // Fallback for builds that omit the numeric keys.
                if (_outTimeUs == 0 && TryTimestamp(value, out var fromText)) _outTimeUs = fromText;
                return null;

            case "frame":
                if (TryLong(value, out var frame)) _frame = frame;
                return null;

            case "fps":
                _fps = TryDouble(value, out var fps) ? fps : null;
                return null;

            case "speed":
                // Formatted as "8.14x", or "N/A" before the first frame.
                _speed = value.EndsWith("x", StringComparison.Ordinal) && TryDouble(value[..^1], out var speed)
                    ? speed
                    : null;
                return null;

            case "total_size":
                _totalSize = TryLong(value, out var size) ? size : null;
                return null;

            case "progress":
                var isFinal = value.SequenceEqual("end");
                return new ProgressUpdate(_outTimeUs, _frame, _fps, _speed, _totalSize, isFinal);

            default:
                return null;
        }
    }

    private static bool TryLong(ReadOnlySpan<char> value, out long result)
    {
        result = 0;
        return !value.SequenceEqual("N/A") && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryDouble(ReadOnlySpan<char> value, out double result)
    {
        result = 0;
        return !value.SequenceEqual("N/A") && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>Parses the <c>HH:MM:SS.ffffff</c> form into microseconds.</summary>
    private static bool TryTimestamp(ReadOnlySpan<char> value, out long microseconds)
    {
        microseconds = 0;
        if (value.SequenceEqual("N/A")) return false;
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span)) return false;

        microseconds = span.Ticks / 10;
        return true;
    }
}

/// <summary>
/// Aggregates progress across the several ffmpeg processes one export runs.
/// </summary>
/// <remarks>
/// <c>out_time</c> is a position on each process's <em>output</em> timeline, so the
/// denominator is the total kept duration, never the source duration.
/// </remarks>
public sealed class ExportProgress(IReadOnlyList<double> stepWeights)
{
    private readonly double _total = stepWeights.Sum();
    private int _completedSteps;
    private double _completedWeight;

    /// <summary>Fraction complete in [0,1].</summary>
    public double Fraction { get; private set; }

    /// <summary>Reports progress within the current step.</summary>
    public void Report(double stepSeconds, double stepDurationSeconds)
    {
        if (_total <= 0) return;

        var within = stepDurationSeconds > 0
            ? Math.Clamp(stepSeconds / stepDurationSeconds, 0, 1)
            : 0;

        var weight = _completedSteps < stepWeights.Count ? stepWeights[_completedSteps] : 0;
        Fraction = Math.Clamp((_completedWeight + (within * weight)) / _total, 0, 1);
    }

    /// <summary>Advances past the current step.</summary>
    public void CompleteStep()
    {
        if (_completedSteps < stepWeights.Count) _completedWeight += stepWeights[_completedSteps];
        _completedSteps++;
        Fraction = Math.Clamp(_completedWeight / _total, 0, 1);
    }
}
