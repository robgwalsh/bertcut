using BertCut.Core.Export;

namespace BertCut.Core.Tests;

public class ProgressParserTests
{
    private static ProgressUpdate FeedBlock(ProgressParser parser, params string[] lines)
    {
        ProgressUpdate? last = null;
        foreach (var line in lines) last ??= null;
        foreach (var line in lines)
        {
            var update = parser.Feed(line);
            if (update is not null) last = update;
        }

        Assert.NotNull(last);
        return last!.Value;
    }

    /// <summary>
    /// ffmpeg prints the same microsecond value for out_time_us and out_time_ms — the
    /// latter has been misnamed for over a decade (see fftools/ffmpeg.c). Reading it as
    /// milliseconds makes every progress bar run 1000x fast.
    /// </summary>
    [Fact]
    public void OutTimeMs_is_parsed_as_microseconds_not_milliseconds()
    {
        var parser = new ProgressParser();

        var update = FeedBlock(parser,
            "frame=720",
            "out_time_ms=24120000",
            "out_time=00:00:24.120000",
            "progress=continue");

        Assert.Equal(24_120_000, update.OutTimeMicroseconds);
        Assert.Equal(24.12, update.OutTime.TotalSeconds, precision: 3);
    }

    [Fact]
    public void OutTimeUs_is_parsed_identically()
    {
        var parser = new ProgressParser();

        var update = FeedBlock(parser, "out_time_us=5000000", "progress=continue");

        Assert.Equal(5.0, update.OutTime.TotalSeconds, precision: 3);
    }

    [Fact]
    public void A_full_realistic_block_is_parsed()
    {
        var parser = new ProgressParser();

        var update = FeedBlock(parser,
            "frame=1234",
            "fps=245.30",
            "stream_0_0_q=23.0",
            "bitrate=4521.7kbits/s",
            "total_size=13631488",
            "out_time_us=24120000",
            "out_time_ms=24120000",
            "out_time=00:00:24.120000",
            "dup_frames=0",
            "drop_frames=0",
            "speed=8.14x",
            "progress=continue");

        Assert.Equal(1234, update.Frame);
        Assert.Equal(245.30, update.Fps!.Value, precision: 2);
        Assert.Equal(8.14, update.SpeedMultiple!.Value, precision: 2);
        Assert.Equal(13_631_488, update.TotalSizeBytes);
        Assert.False(update.IsFinal);
    }

    /// <summary>
    /// Every numeric field is literally "N/A" in the first block of every encode, before
    /// any frame has been written.
    /// </summary>
    [Fact]
    public void Not_available_values_do_not_throw_and_leave_fields_unset()
    {
        var parser = new ProgressParser();

        var update = FeedBlock(parser,
            "frame=0",
            "fps=N/A",
            "bitrate=N/A",
            "total_size=N/A",
            "out_time_us=N/A",
            "out_time_ms=N/A",
            "out_time=N/A",
            "speed=N/A",
            "progress=continue");

        Assert.Equal(0, update.OutTimeMicroseconds);
        Assert.Null(update.Fps);
        Assert.Null(update.SpeedMultiple);
        Assert.Null(update.TotalSizeBytes);
    }

    [Fact]
    public void The_final_block_is_flagged()
    {
        var parser = new ProgressParser();

        var update = FeedBlock(parser, "out_time_us=30000000", "progress=end");

        Assert.True(update.IsFinal);
    }

    [Fact]
    public void Values_carry_forward_across_blocks()
    {
        var parser = new ProgressParser();

        FeedBlock(parser, "frame=100", "out_time_us=1000000", "speed=5.0x", "progress=continue");
        var second = FeedBlock(parser, "out_time_us=2000000", "progress=continue");

        Assert.Equal(100, second.Frame);
        Assert.Equal(2.0, second.OutTime.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Lines_without_an_equals_sign_are_ignored()
    {
        var parser = new ProgressParser();

        Assert.Null(parser.Feed(""));
        Assert.Null(parser.Feed("garbage"));
        Assert.Null(parser.Feed("=leading"));
    }

    [Fact]
    public void The_timestamp_string_is_used_when_numeric_keys_are_absent()
    {
        var parser = new ProgressParser();

        var update = FeedBlock(parser, "out_time=00:01:02.500000", "progress=continue");

        Assert.Equal(62.5, update.OutTime.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Aggregate_progress_advances_across_weighted_steps()
    {
        var progress = new ExportProgress([10.0, 10.0, 5.0]);

        progress.Report(5, 10);
        Assert.Equal(0.2, progress.Fraction, precision: 3);   // half of the first 10 of 25

        progress.CompleteStep();
        Assert.Equal(0.4, progress.Fraction, precision: 3);

        progress.CompleteStep();
        progress.CompleteStep();
        Assert.Equal(1.0, progress.Fraction, precision: 3);
    }
}
