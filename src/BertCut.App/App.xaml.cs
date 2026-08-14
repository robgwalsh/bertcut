using System.IO;
using System.Windows;
using BertCut.Media;
using BertCut.Media.Decode;

namespace BertCut.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
                "Run tools/fetch-ffmpeg.ps1 from the repository root to install it.",
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
    }
}
