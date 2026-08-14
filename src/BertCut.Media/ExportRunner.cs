using System.Diagnostics;
using BertCut.Core.Export;

namespace BertCut.Media;

/// <summary>Progress of a running export.</summary>
public readonly record struct ExportStatus(double Fraction, string Description, double? SpeedMultiple);

/// <summary>Raised when ffmpeg exits non-zero.</summary>
public sealed class FfmpegException(string message) : Exception(message);

/// <summary>
/// Executes an <see cref="ExportPlan"/> by running its ffmpeg steps in order.
/// </summary>
public sealed class ExportRunner(FfmpegRuntime runtime)
{
    /// <summary>How many stderr lines to keep for the failure message.</summary>
    private const int StderrTailLines = 50;

    public async Task RunAsync(
        ExportPlan plan,
        string tempDirectory,
        IProgress<ExportStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(tempDirectory);

        var weights = plan.Steps.Select(s => Math.Max(0.001, s.DurationSeconds)).ToArray();
        var aggregate = new ExportProgress(weights);

        try
        {
            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The concat demuxer reads a list file that only exists once the segments
                // it names have been produced, so it is written just before its step runs.
                WriteConcatListIfNeeded(step, plan, tempDirectory);

                await RunStepAsync(step, aggregate, progress, cancellationToken).ConfigureAwait(false);

                aggregate.CompleteStep();
                progress?.Report(new ExportStatus(aggregate.Fraction, step.Description, null));
            }
        }
        finally
        {
            CleanUp(plan);
        }
    }

    private async Task RunStepAsync(
        FfmpegStep step,
        ExportProgress aggregate,
        IProgress<ExportStatus>? progress,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(runtime.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Redirected so cancellation can request a graceful stop by writing "q".
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtime.FfmpegPath)!,
        };

        // ArgumentList applies the Windows quoting rules itself. Filter graphs contain
        // ':' ',' ';' '[' ']' and drive colons, so hand-built command lines misquote them.
        foreach (var argument in step.Arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new FfmpegException($"Failed to start ffmpeg for: {step.Description}");

        var stderrTail = new Queue<string>(StderrTailLines);
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
            {
                if (stderrTail.Count == StderrTailLines) stderrTail.Dequeue();
                stderrTail.Enqueue(line);
            }
        }, CancellationToken.None);

        var parser = new ProgressParser();

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (parser.Feed(line) is not { } update) continue;

                aggregate.Report(update.OutTime.TotalSeconds, step.DurationSeconds);
                progress?.Report(new ExportStatus(aggregate.Fraction, step.Description, update.SpeedMultiple));
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopGracefullyAsync(process).ConfigureAwait(false);
            throw;
        }

        await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new FfmpegException(
                $"{step.Description} failed (exit code {process.ExitCode})." +
                Environment.NewLine + string.Join(Environment.NewLine, stderrTail));
        }
    }

    /// <summary>
    /// Asks ffmpeg to stop cleanly before killing it, so it finalizes the container it is
    /// writing rather than leaving a truncated file behind.
    /// </summary>
    private static async Task StopGracefullyAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }

            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            // Graceful stop declined; fall through to the kill below.
        }

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static void WriteConcatListIfNeeded(FfmpegStep step, ExportPlan plan, string tempDirectory)
    {
        var listIndex = -1;
        for (var i = 0; i < step.Arguments.Count - 1; i++)
            if (step.Arguments[i] == "-i" && step.Arguments[i + 1].EndsWith("segments.txt", StringComparison.Ordinal))
                listIndex = i + 1;

        if (listIndex < 0) return;

        var segments = plan.Steps
            .Select(s => s.WritesFile)
            .Where(f => f is not null && Path.GetFileName(f).StartsWith("seg", StringComparison.Ordinal))
            .Select(f => f!)
            .ToList();

        File.WriteAllText(step.Arguments[listIndex], FfmpegArgs.ConcatListFile(segments));
    }

    private static void CleanUp(ExportPlan plan)
    {
        foreach (var file in plan.TempFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp file is not worth failing an otherwise finished export.
            }
        }
    }
}
