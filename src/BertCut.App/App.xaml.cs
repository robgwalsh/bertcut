using System.IO;
using System.Windows;
using BertCut.Core.Session;
using BertCut.Media;
using BertCut.Media.Audio;
using BertCut.Media.Decode;
using Velopack;

namespace BertCut.App;

public partial class App : Application
{
    [STAThread]
    private static void Main()
    {
        // Must run before any WPF code: this handles Velopack's install, update and uninstall
        // hooks, which re-launch this exe and expect the process to exit again immediately.
        // Anything above it runs during an install.
        VelopackApp.Build().Run();

        // After Velopack's hooks, which must never wait on another copy, and before any WPF:
        // a launch that is only handing its file to the editor already open should cost a
        // pipe write, not a window.
        var launch = LaunchArgs.Parse(Environment.GetCommandLineArgs()[1..], Environment.CurrentDirectory);

        using var instance = SingleInstance.TryClaim();
        if (instance is null && SingleInstance.HandOff(launch)) return;

        var app = new App { _launch = launch, _instance = instance };
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>What this launch asked for. Empty under the harness, which never calls <c>Main</c>.</summary>
    private LaunchArgs _launch = LaunchArgs.None;

    /// <summary>
    /// This copy's claim on the state root, or null when it started beside a copy that would
    /// not answer — in which case it takes no requests, because the other one still owns the pipe.
    /// </summary>
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything reads a session. State used to live in %LOCALAPPDATA%\BertCut, which is
        // now the Velopack install directory and emptied by every update.
        AppPaths.MigrateLegacyData();

        FfmpegRuntime runtime;

        try
        {
            runtime = FfmpegRuntime.Locate();

            // Points the in-process decoder at the same build the export path shells out
            // to, so the preview and the exported file cannot disagree at the codec level.
            FfmpegLoader.EnsureInitialized(runtime);
        }
        catch (Exception error) when (error is FileNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            MessageBox.Show(
                error.Message + Environment.NewLine + Environment.NewLine +
                "Running from a source tree? Run tools/fetch-ffmpeg.ps1 from the repository root." +
                Environment.NewLine +
                "Installed BertCut? This copy is incomplete — reinstall it from " +
                "https://github.com/robgwalsh/bertcut/releases/latest.",
                "BertCut — FFmpeg not found",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
            return;
        }

        // Neither of these blocks a user action, and both are bounded. The purge reclaims what
        // earlier versions left behind; the trim is what keeps the envelope cache from growing
        // without end, since nothing else ever deletes one.
        _ = Task.Run(() =>
        {
            FilmstripCache.PurgeThumbnails();
            AudioPeaksCache.Trim();
        });

        // BertBrowser when it is installed, the Windows dialog when it is not — decided again
        // at every question, so neither app has to be restarted for the other.
        var window = new MainWindow(
            runtime,
            picker: owner => new BertBrowserPicker((MainWindow)owner, new WindowsDialogPicker(owner)));
        MainWindow = window;
        window.Show();

        // Opening straight into a file passed on the command line makes "right-click a
        // recording, open with BertCut" a one-step workflow.
        if (_launch.Path is { } path && File.Exists(path))
            _ = window.Dispatcher.InvokeAsync(() => window.OpenPath(path));

        // From here a second launch reaches this window instead of becoming one of its own.
        _instance?.Listen(handed => window.Dispatcher.InvokeAsync(() => window.HandOff(handed)));

        // Last, and on a background thread: the window is already up, and a copy that was never
        // installed returns from this immediately.
        _ = Task.Run(() => new UpdateService().CheckAndStageUpdateAsync());
    }
}
