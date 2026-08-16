using System.IO;
using System.Windows;
using BertCut.Core.Session;
using BertCut.Media;
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

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

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

        // The timeline no longer draws thumbnails, so nothing reads this cache and nothing
        // writes it — but earlier versions could have left gigabytes of it on disk. A zero
        // budget reclaims that, on a background thread, without blocking a user action.
        _ = Task.Run(() => FilmstripCache.Trim(budgetBytes: 0));

        var window = new MainWindow(runtime);
        MainWindow = window;
        window.Show();

        // Opening straight into a file passed on the command line makes "right-click a
        // recording, open with BertCut" a one-step workflow.
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            _ = window.Dispatcher.InvokeAsync(() => window.OpenPath(e.Args[0]));

        // Last, and on a background thread: the window is already up, and a copy that was never
        // installed returns from this immediately.
        _ = Task.Run(() => new UpdateService().CheckAndStageUpdateAsync());
    }
}
