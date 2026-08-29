using System.Runtime.CompilerServices;
using BertCut.Core.Session;

namespace BertCut.Core.Tests;

/// <summary>
/// Points every test in this assembly at a scratch state root before any of them runs.
/// </summary>
/// <remarks>
/// <para>
/// The harness has always done this per run, for the reason given in CLAUDE.md: the app restores
/// by content key, so without isolation a run leaves its edits waiting in the user's editor. The
/// test suite did not need it while nothing under <c>AppPaths</c> was written except by tests that
/// set the variable themselves.
/// </para>
/// <para>
/// That stopped being true when <see cref="BertCut.Media.SourceIndexCache"/> and
/// <see cref="BertCut.Media.FfmpegCapabilityCache"/> were added: <c>MediaProber.ProbeAsync</c> and
/// <c>FfmpegRuntime.Locate</c> now write a cache as an ordinary part of doing their job, and every
/// test that probes a temporary clip was dropping an <c>index.bin</c> into
/// <c>%USERPROFILE%\.bertcut\cache</c> for a file that had already been deleted. Harmless, and
/// still not the suite's to write.
/// </para>
/// <para>
/// A module initializer rather than a fixture, because it has to be in place before the first
/// test class is constructed — several of them locate ffmpeg in their constructor. Tests that
/// manage the variable themselves still work: they save and restore whatever they found, which is
/// now this directory instead of the user's.
/// </para>
/// </remarks>
internal static class TestStateRoot
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        // An outer runner may already have isolated us; never override a deliberate choice.
        if (Environment.GetEnvironmentVariable(AppPaths.OverrideVariable) is { Length: > 0 }) return;

        var root = Path.Combine(
            Path.GetTempPath(), "bertcut-tests", Guid.NewGuid().ToString("n"));

        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        };
    }
}
