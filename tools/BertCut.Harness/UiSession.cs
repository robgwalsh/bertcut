using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BertCut.App;
using BertCut.Core.Input;
using AppShell = BertCut.App.App;
using BertCut.Core.Session;
using BertCut.Media;
using BertCut.Media.Decode;

namespace BertCut.Harness;

/// <summary>
/// The real editor window, hosted where the user cannot see it and cannot be interrupted by it.
/// </summary>
/// <remarks>
/// <para>
/// The window is shown, laid out and rendered exactly as in production — it is simply parked
/// outside every monitor's coordinate space and refused activation. That matters because
/// <see cref="System.Windows.Media.Imaging.RenderTargetBitmap"/> re-renders the visual tree
/// through the software rasteriser rather than grabbing the screen, so where the window sits,
/// what covers it, and whether the compositor ever presents it are all irrelevant to what a
/// capture contains.
/// </para>
/// <para>
/// Nothing here synthesises operating-system input. Keys are resolved through the real key map
/// and handed to the real dispatch point, so the app under test is the app the user runs, but
/// the keystrokes exist only inside this process and cannot land in whatever the user is
/// typing into.
/// </para>
/// </remarks>
internal sealed class UiSession : IDisposable
{
    private readonly HarnessOptions _options;

    private readonly ForegroundGuard _guard;

    private UiSession(HarnessOptions options, MainWindow window, FfmpegRuntime runtime, ForegroundGuard guard)
    {
        _options = options;
        Window = window;
        Runtime = runtime;
        _guard = guard;
    }

    public MainWindow Window { get; }

    /// <summary>The codec build the window is decoding with, so samples are encoded by the same one.</summary>
    public FfmpegRuntime Runtime { get; }

    public EditorViewModel Model => Window.Model;

    public Dispatcher Dispatcher => Window.Dispatcher;

    /// <summary>Where the window was parked. Far enough out that no monitor arrangement reaches it.</summary>
    private const int Offscreen = -32000;

    /// <summary>
    /// Boots WPF, the codec runtime and the window, on the calling STA thread.
    /// </summary>
    public static UiSession Start(HarnessOptions options, TextWriter log)
    {
        // Set before anything touches a store: every path in the app reads this on access,
        // so a run cannot inherit the user's sessions or overwrite their key bindings.
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, options.StateDir);
        Directory.CreateDirectory(options.StateDir);

        // A window nobody presents has no use for a GPU, and the software rasteriser is what
        // captures go through anyway. Taking the hardware path out removes driver and
        // render-tier variance from the pictures.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        var runtime = FfmpegRuntime.Locate();
        FfmpegLoader.EnsureInitialized(runtime);

        // InitializeComponent merges Theme.xaml at application scope exactly as production
        // does. Run() is what would call OnStartup — the ffmpeg message box, the cache trim
        // and a visible window all live there, and none of it should happen here.
        var app = new AppShell();
        app.InitializeComponent();

        if (Application.Current.TryFindResource("KeyCap") is null)
            throw new InvalidOperationException(
                "Theme.xaml did not load at application scope; every capture would show an unstyled window.");

        var before = Native.Foreground();
        if (options.Verbose) log.WriteLine($"# foreground on entry: {before.Describe()}");

        // Started before the window exists, so the one activation that does happen — during
        // the first layout pass, through no event this process is told about — is corrected
        // within a tick rather than lasting until the run settles.
        var guard = ForegroundGuard.Start(before.Handle, log);

        var window = new MainWindow(runtime);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = Offscreen;
        window.Top = Offscreen;
        window.SourceInitialized += (_, _) => Native.MakeNonInteractive(window);

        // The editor focuses itself once loaded — bare letter keys are its whole interface, so
        // it must. Refusing focus at the WPF level keeps that from reaching SetFocus, which
        // activates a window even when it is disabled and marked WS_EX_NOACTIVATE. Nothing
        // about how the window draws depends on this.
        window.Focusable = false;
        window.Activated += (_, _) => Native.RestoreForeground(before.Handle);

        window.Show();

        var session = new UiSession(options, window, runtime, guard);
        session.Settle();

        var after = Native.Foreground();
        if (after.Handle != before.Handle)
            log.WriteLine($"# WARNING: the foreground is {after.Describe()}, not where it started.");

        return session;
    }

    /// <summary>
    /// Runs the app forward until it has nothing left to do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="EditorViewModel.IsBusy"/> is the only signal that an open or import has
    /// finished: it brackets every await in those paths, and the ffprobe round-trip behind
    /// them outlasts any number of empty dispatcher passes. Pumping at
    /// <see cref="DispatcherPriority.Background"/> is what lets the continuations waiting on
    /// the captured synchronization context run at all.
    /// </para>
    /// <para>
    /// The final pass at <see cref="DispatcherPriority.ContextIdle"/> returns only once
    /// layout and render have been through, which is the state a capture should see.
    /// </para>
    /// </remarks>
    public void Settle(int quietMs = 0)
    {
        var clock = Stopwatch.StartNew();

        while (Model.IsBusy && clock.ElapsedMilliseconds < _options.BusyTimeoutMs)
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        if (Model.IsBusy)
            throw new TimeoutException(
                $"The editor was still busy after {_options.BusyTimeoutMs} ms.");

        if (quietMs > 0)
        {
            var quiet = Stopwatch.StartNew();
            while (quiet.ElapsedMilliseconds < quietMs)
            {
                Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(5);
            }
        }

        Window.UpdateLayout();
        Window.SettleAnimations();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>Resolves a gesture through the live key map and dispatches what it means.</summary>
    /// <returns>The intent that ran, or <c>None</c> when the gesture is unbound in this mode.</returns>
    public EditorIntent PressKey(EditorKey key, EditorModifiers modifiers)
    {
        var intent = Window.Bindings.Resolve(key, modifiers, Model.Mode);
        if (intent != EditorIntent.None) Window.Invoke(intent);

        return intent;
    }

    /// <summary>Carries out an intent through the window's own dispatch point.</summary>
    public void Dispatch(EditorIntent intent) => Window.Invoke(intent);

    /// <summary>The composited video frame, with no interface around it.</summary>
    public DecodedFrame? PreviewFrame =>
        Model.Preview is { HasFrame: true } preview ? preview.Canvas : null;

    public void Dispose()
    {
        // Closing rather than abandoning, so the session flush in OnClosing runs — into the
        // scratch state directory, which is the only place this run has written.
        try
        {
            Dispatcher.Invoke(Window.Close);
        }
        catch (Exception e) when (e is InvalidOperationException or TaskCanceledException)
        {
            // The dispatcher is already going down; nothing left worth saving.
        }

        Dispatcher.InvokeShutdown();
        _guard.Dispose();
    }
}
