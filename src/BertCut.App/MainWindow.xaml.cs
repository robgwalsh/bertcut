using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BertCut.Core.Export;
using BertCut.Core.Input;
using BertCut.Core.Session;
using BertCut.Media;
using BertCut.Media.Audio;
using BertCut.Media.Decode;
using Microsoft.Win32;

namespace BertCut.App;

public partial class MainWindow : Window
{
    private readonly FfmpegRuntime _runtime;
    private readonly EditorViewModel _model;
    private WriteableBitmap? _surface;

    /// <summary>
    /// The keyboard, as the user has it.
    /// </summary>
    /// <remarks>
    /// Loaded from disk rather than taken from <see cref="KeyMap"/>, and replaced wholesale
    /// when the Controls page changes something. Everything that prints a key — the
    /// tooltips, the help sheet, the hint over an empty preview — is rebuilt from this, so
    /// there is exactly one answer in the app to "what does this key do".
    /// </remarks>
    private KeyBindings _keys = KeyBindingStore.Load();

    /// <param name="audioOutput">
    /// Where preview audio goes. The app leaves this null and gets the sound card; the
    /// harness passes a silent one, so a scripted run is no more audible than it is visible.
    /// </param>
    public MainWindow(FfmpegRuntime runtime, Func<IAudioOutput>? audioOutput = null)
    {
        InitializeComponent();

        // Which build this is, and with it which channel — "BertCut 1.2.3" against
        // "BertCut 1.2.4-unstable.42". The title bar is outside the harness's client-area
        // captures, so this cannot make a screenshot version-dependent.
        Title = $"BertCut {AppVersion.Display}";

        _runtime = runtime;
        _model = new EditorViewModel(runtime, audioOutput);
        _model.PropertyChanged += OnModelChanged;
        _model.FrameChanged += Present;
        _model.SessionRestored += ShowRestoreToast;

        Timeline.Bind(_model);
        Placement.Bind(_model);

        Settings.BindingsChanged += OnBindingsChanged;
        Settings.CloseRequested += HideSettings;
        ApplyBindings();

        // Playback advances on the composition tick so frames land in step with WPF's
        // own rendering rather than racing it from a timer. It is also where a frame the
        // pump finished between ticks gets picked up.
        CompositionTarget.Rendering += (_, _) => _model.Tick();

        // How much detail the preview is worth compositing is a question about the pane, so
        // it is answered here and re-answered whenever the pane changes size.
        PreviewPane.SizeChanged += (_, _) => ApplyPreviewSize();
        DpiChanged += (_, _) => ApplyPreviewSize();

        Loaded += (_, _) => Keyboard.Focus(this);
    }

    /// <summary>
    /// Routes every keystroke through the key map.
    /// </summary>
    /// <remarks>
    /// A single PreviewKeyDown on the window, rather than InputBindings, because bare
    /// letter keys are the core of this editor's speed and any focused control would
    /// swallow them. Nothing in the editing surface is focusable, which is what makes this
    /// safe.
    /// </remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // The Controls page owns the keyboard while it is open. It is the one screen where
        // a keystroke is being chosen rather than obeyed, and an editor that also acted on
        // it would ripple a cut away while you were binding the key that does it.
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            e.Handled = Settings.HandleKey(e);
            return;
        }

        if (HelpOverlay.Visibility == Visibility.Visible
            && e.Key is Key.Escape or Key.F1)
        {
            HideHelp();
            e.Handled = true;
            return;
        }

        var key = WpfKeys.Translate(e);
        if (key == EditorKey.None) return;

        var intent = _keys.Resolve(key, WpfKeys.Modifiers(), _model.Mode);
        if (intent == EditorIntent.None) return;

        e.Handled = true;

        // < and > are dual-purpose: a tap steps one frame, and holding hands the playhead
        // over to normal-speed playback the moment the keyboard's auto-repeat starts. Doing
        // it on the repeat rather than on a timer means the two behaviours are separated by
        // exactly the delay the user has already tuned for every other held key.
        if (key is EditorKey.Comma or EditorKey.Period
            && intent is EditorIntent.StepBack or EditorIntent.StepForward)
        {
            if (e.IsRepeat) _model.HoldShuttle(intent == EditorIntent.StepForward ? 1 : -1);
            else _model.Dispatch(intent);
            return;
        }

        Invoke(intent);
    }

    /// <summary>
    /// Carries out an intent, whether a key or a toolbar button asked for it.
    /// </summary>
    /// <remarks>
    /// The single place the two halves of the app meet. A button that called the view model
    /// directly would be a second implementation of the same action, free to drift from the
    /// key that is supposed to be its equal. The UI harness enters here too, for the same
    /// reason: a test that reached past this would be testing something the user cannot do.
    /// </remarks>
    internal void Invoke(EditorIntent intent)
    {
        switch (intent)
        {
            case EditorIntent.OpenFile: OpenFile(); break;
            case EditorIntent.ImportSource: ImportFile(); break;
            case EditorIntent.ChooseOverlayFile: ChooseOverlayFile(); break;
            case EditorIntent.AppendSource: AppendFile(); break;
            case EditorIntent.Export: _ = ExportAsync(); break;
            case EditorIntent.ToggleHelp: ToggleHelp(); break;
            case EditorIntent.ToggleSettings: ToggleSettings(); break;
            default: _model.Dispatch(intent); break;
        }
    }

    /// <summary>Stops the run that holding a frame-step key started.</summary>
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);

        if (WpfKeys.Translate(e) is not (EditorKey.Comma or EditorKey.Period)) return;
        if (_model.Mode != EditorMode.Normal) return;

        // Harmless after a tap, which left the transport stopped anyway.
        _model.ReleaseShuttle();
        e.Handled = true;
    }

    /// <summary>
    /// Any click anywhere puts the restore notice away.
    /// </summary>
    /// <remarks>
    /// Deliberately not handled: this is a dismissal riding along on whatever the click was
    /// actually for. Clicking the notice away and starting a crop drag are the same gesture,
    /// and asking for two would make the notice a thing to get past.
    /// </remarks>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        HideRestoreToast();
    }

    /// <summary>Opens a file directly, e.g. one passed on the command line.</summary>
    public void OpenPath(string path) => Open(path);

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a video",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v|All files|*.*",
        };

        if (dialog.ShowDialog(this) == true) Open(dialog.FileName);
    }

    /// <summary>
    /// Opens a video, clearing any notice left over from the last one.
    /// </summary>
    /// <remarks>
    /// The open itself does not raise the notice again unless there is something to restore,
    /// so a stale one would otherwise sit there claiming edits that belong to a video no
    /// longer on screen.
    /// </remarks>
    private void Open(string path)
    {
        HideRestoreToast();
        _ = _model.OpenAsync(path);
    }

    private void ImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a video to overlay",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v|All files|*.*",
        };

        if (dialog.ShowDialog(this) == true) _ = _model.ImportAsync(dialog.FileName);
    }

    /// <summary>The overlay source card's third row: a file, brought in and placed whole.</summary>
    private void ChooseOverlayFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a video to overlay",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v|All files|*.*",
        };

        // Backing out of the picker leaves the card up rather than abandoning the overlay:
        // the user answered "not that file", not "never mind".
        if (dialog.ShowDialog(this) == true) _ = _model.ImportAndOverlayAsync(dialog.FileName);
    }

    /// <summary>
    /// Adds a video: the first one, or another onto the end of the timeline.
    /// </summary>
    /// <remarks>
    /// This is the toolbar's front door — the one button that says what it is — so on an
    /// empty editor it has to read as "open a video" rather than as an appending operation
    /// against a timeline that does not exist yet. <c>AppendAsync</c> already falls through
    /// to an open in that case; the title is what tells the user so before they commit.
    /// </remarks>
    private void AppendFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = _model.HasMedia ? "Add a video to the end of the timeline" : "Open a video",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v|All files|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        HideRestoreToast();
        _ = _model.AppendAsync(dialog.FileName);
    }

    // ---- toolbar ---------------------------------------------------------------------

    /// <summary>Every icon button but the reset, which has no intent by design.</summary>
    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EditorIntent intent }) Invoke(intent);
    }

    /// <summary>
    /// Confirms, then throws every edit away.
    /// </summary>
    /// <remarks>
    /// The prompt is here rather than in the view model so the confirmation is a UI
    /// decision and <c>ResetAll</c> stays callable without one. It defaults to No: this is
    /// the only button in the app that destroys work, and it sits a few pixels from one
    /// that does not.
    /// </remarks>
    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (!_model.HasMedia)
        {
            StatusLabel.Text = "Nothing to reset — Ctrl+O to open a video.";
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Reset everything?\n\n"
            + "Every cut, crop, overlay and added clip is discarded and the timeline goes "
            + "back to the video as you opened it.\n\n"
            + "Ctrl+Z will still bring it all back.",
            "BertCut",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes) _model.ResetAll();

        Keyboard.Focus(this);
    }

    /// <summary>
    /// Confirms, then closes everything and releases the video files.
    /// </summary>
    /// <remarks>
    /// The prompt is here for the same reason the reset's is — a confirmation is a UI
    /// decision, and <c>CloseAll</c> stays callable without one. It defaults to No. Unlike
    /// the reset, though, this one is not undoable: closing is not an edit, and afterwards
    /// there is no document left for Ctrl+Z to act on. What survives is the autosave, which
    /// is what the prompt promises and what makes this recoverable at all.
    /// </remarks>
    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (!_model.HasMedia)
        {
            StatusLabel.Text = "Nothing open.";
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Close everything?\n\n"
            + "The window is emptied and BertCut lets go of the video files, so you can "
            + "move, rename or delete them.\n\n"
            + "Your edits are saved against each video first — open one again and they "
            + "come back — but Ctrl+Z will not bring this window back.",
            "BertCut",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes) ClearWorkspace();

        Keyboard.Focus(this);
    }

    /// <summary>
    /// Puts the shell back to how it starts, and drops its own hold on the last frame.
    /// </summary>
    /// <remarks>
    /// The bitmap matters. <c>Present</c> only builds a new surface when the frame size
    /// changes, so a stale one would be kept — and shown — until a video of a different
    /// size was opened. Clearing it here is also what brings the opening hint back.
    /// </remarks>
    internal void ClearWorkspace()
    {
        HideRestoreToast();

        _model.CloseAll();

        _surface = null;
        PreviewImage.Source = null;
        EmptyHint.Visibility = Visibility.Visible;
    }

    private void OnHelpCloseClick(object sender, RoutedEventArgs e) => HideHelp();

    /// <summary>Closes the sheet when the dimmed area around it is clicked, not the sheet itself.</summary>
    private void OnHelpBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, HelpOverlay)) HideHelp();
    }

    private void ToggleHelp()
    {
        if (HelpOverlay.Visibility == Visibility.Visible) HideHelp();
        else ShowHelp();
    }

    private void ShowHelp()
    {
        HideSettings();
        HelpOverlay.Visibility = Visibility.Visible;

        // The sheet lifts in rather than appearing. It costs a quarter of a second and it
        // is the difference between a panel and a thing that opened.
        if (TryFindResource("HelpIn") is Storyboard entrance) entrance.Begin(this);
    }

    private void HideHelp()
    {
        HelpOverlay.Visibility = Visibility.Collapsed;
        Keyboard.Focus(this);
    }

    // ---- the overlay source card -------------------------------------------------------

    /// <summary>
    /// Puts the card up or down to match the mode, and says what each row would take.
    /// </summary>
    /// <remarks>
    /// Driven from the model's mode rather than called at the point <c>P</c> is pressed, so
    /// the card cannot be left up by a path that changes the mode some other way — a cancel,
    /// a commit, or the editor being closed out from under it.
    /// </remarks>
    private void SyncOverlaySourceCard()
    {
        var wanted = _model.IsChoosingOverlaySource;
        var showing = OverlaySourceOverlay.Visibility == Visibility.Visible;

        if (wanted == showing) return;

        if (!wanted)
        {
            OverlaySourceOverlay.Visibility = Visibility.Collapsed;
            Keyboard.Focus(this);
            return;
        }

        // A row that cannot be taken is dimmed and says why, rather than failing at the
        // moment it is pressed: what is on offer is the whole question the card is asking.
        var range = _model.SelectedRange;
        OverlaySourceRange.IsEnabled = range is not null;
        OverlaySourceRangeDetail.Text = range is { } marked
            ? $"{marked.Length} frames of the base video, from where it is marked."
            : "Nothing is marked — press I and O on the timeline first.";

        var segment = _model.SelectedSegment;
        OverlaySourceSegment.IsEnabled = segment is not null;
        OverlaySourceSegmentDetail.Text = segment is { } index
            ? $"{_model.Project.Base[index].LengthFrames} frames — segment {index + 1} of "
              + $"{_model.Project.Base.Length}, whole."
            : "No segment selected — click one on the track first.";

        HideHelp();
        HideSettings();
        OverlaySourceOverlay.Visibility = Visibility.Visible;

        if (TryFindResource("OverlaySourceIn") is Storyboard entrance) entrance.Begin(this);
    }

    /// <summary>Backing out by clicking the dimmed area, as the other two panels do.</summary>
    private void OnOverlaySourceBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, OverlaySourceOverlay)) Invoke(EditorIntent.Cancel);
    }

    /// <summary>
    /// A row pressed with the mouse, which is the same thing as the digit printed on it.
    /// </summary>
    private void OnOverlaySourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EditorIntent intent }) Invoke(intent);
    }

    // ---- the restore notice ------------------------------------------------------------

    /// <summary>
    /// Which showing of the notice is current.
    /// </summary>
    /// <remarks>
    /// The fade-out collapses the notice when it finishes, and a second video can be opened
    /// inside that fifth of a second. Without this the old fade's completion would collapse
    /// the new notice the moment it appeared.
    /// </remarks>
    private int _toastGeneration;

    /// <summary>Says out loud that this video came back with the edits made to it last time.</summary>
    private void ShowRestoreToast(string fileName)
    {
        RestoreToastTitle.Text = $"Restored your previous edits to {fileName}";

        // The undo key can have been moved, or taken off the keyboard entirely, in which
        // case the notice offers the only other way out of it rather than naming a key that
        // does nothing.
        var undo = Gesture(EditorIntent.Undo);

        RestoreToastKey.Content = undo;
        RestoreToastKey.Visibility = undo.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RestoreToastHint.Margin = new Thickness(undo.Length == 0 ? 0 : 9, 0, 0, 0);
        RestoreToastHint.Text = undo.Length == 0
            ? "Click anywhere to dismiss — or use Reset to start over."
            : "discards them · click anywhere to dismiss";

        _toastGeneration++;
        RestoreToast.Visibility = Visibility.Visible;

        RestoreToast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(170)));

        // Rises as it fades in, the same way the help sheet and the Controls page do. A
        // panel that simply exists reads as something that was always there and missed.
        Lift(RestoreToast).BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, Ms(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Fades the notice out, if one is up. Safe to call when none is.</summary>
    private void HideRestoreToast()
    {
        if (RestoreToast.Visibility != Visibility.Visible) return;

        var generation = ++_toastGeneration;

        var fade = new DoubleAnimation(0, Ms(140));
        fade.Completed += (_, _) =>
        {
            if (_toastGeneration == generation) RestoreToast.Visibility = Visibility.Collapsed;
        };

        RestoreToast.BeginAnimation(OpacityProperty, fade);
    }

    private static Duration Ms(int milliseconds) => new(TimeSpan.FromMilliseconds(milliseconds));

    private static TranslateTransform Lift(UIElement element) => (TranslateTransform)element.RenderTransform;

    // ---- harness ---------------------------------------------------------------------

    /// <summary>Editor state, for a harness driving this window.</summary>
    internal EditorViewModel Model => _model;

    /// <summary>
    /// The keyboard as this window currently has it.
    /// </summary>
    /// <remarks>
    /// Exposed rather than reloaded from disk so a harness pressing a key resolves it through
    /// the same map the window is dispatching with, including anything the Controls page
    /// changed a moment ago.
    /// </remarks>
    internal KeyBindings Bindings => _keys;

    /// <summary>
    /// Jumps every entrance animation to its finished state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Screenshots are otherwise a sample of a 260 ms cubic ease, and two runs of the same
    /// script disagree about the opacity of the help sheet. Removing an animation reverts the
    /// property to its base value, which for all of these is exactly the value the storyboard
    /// was travelling towards.
    /// </para>
    /// <para>
    /// This settles a fade rather than finishing it: cancelling the toast's fade-out skips
    /// the <c>Completed</c> handler that collapses it, so this is a thing to call before
    /// capturing, never a way to dismiss the notice.
    /// </para>
    /// </remarks>
    internal void SettleAnimations()
    {
        HelpCard.BeginAnimation(OpacityProperty, null);
        Lift(HelpCard).BeginAnimation(TranslateTransform.YProperty, null);

        Settings.BeginAnimation(OpacityProperty, null);
        Lift(Settings).BeginAnimation(TranslateTransform.YProperty, null);

        OverlaySourceCard.BeginAnimation(OpacityProperty, null);
        Lift(OverlaySourceCard).BeginAnimation(TranslateTransform.YProperty, null);

        RestoreToast.BeginAnimation(OpacityProperty, null);
        Lift(RestoreToast).BeginAnimation(TranslateTransform.YProperty, null);
    }

    // ---- settings --------------------------------------------------------------------

    private void ToggleSettings()
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) HideSettings();
        else ShowSettings();
    }

    private void ShowSettings()
    {
        HelpOverlay.Visibility = Visibility.Collapsed;

        Settings.Open(_keys);
        SettingsOverlay.Visibility = Visibility.Visible;

        if (TryFindResource("SettingsIn") is Storyboard entrance) entrance.Begin(this);
    }

    private void HideSettings()
    {
        if (SettingsOverlay.Visibility != Visibility.Visible) return;

        SettingsOverlay.Visibility = Visibility.Collapsed;

        // Focus went nowhere while the screen was up — nothing on it is focusable either —
        // but a rebind that ended on Escape leaves the page armed, and it must not still be
        // armed the next time it opens.
        Settings.Close();
        Keyboard.Focus(this);
    }

    /// <summary>Closes the screen when the dimmed area around it is clicked, not the screen itself.</summary>
    private void OnSettingsBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsOverlay)) HideSettings();
    }

    /// <summary>
    /// Takes the Controls page's changes, everywhere they show.
    /// </summary>
    /// <remarks>
    /// Saved on the spot rather than behind an OK button, like every other change this app
    /// makes. A failed write is worth saying out loud — the next session would silently go
    /// back to the shipped keys — but it must not stop the keys working now.
    /// </remarks>
    private void OnBindingsChanged(KeyBindings bindings)
    {
        _keys = bindings;
        ApplyBindings();

        try
        {
            KeyBindingStore.Save(bindings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusLabel.Text = "Could not save your controls — they will work until you close BertCut.";
        }
    }

    /// <summary>Rebuilds everything in the shell that prints a key.</summary>
    private void ApplyBindings()
    {
        BuildHelp();
        BuildToolTips();

        // After BuildToolTips, which would otherwise overwrite this button's tooltip with
        // one built from a name that no longer matches its glyph.
        ApplyMuteGlyph();

        EmptyHint.Text =
            $"{Gesture(EditorIntent.OpenFile)} to open a video\n\n"
            + $"{Gesture(EditorIntent.MarkIn)} / {Gesture(EditorIntent.MarkOut)} mark in and out · "
            + $"{Gesture(EditorIntent.RippleDelete)} ripples the marked range away\n"
            + $"{Gesture(EditorIntent.BeginCrop)} crops the marked range · "
            + $"{Gesture(EditorIntent.BeginOverlay)} overlays a clip\n"
            + $"{Gesture(EditorIntent.PlayPause)} plays · "
            + $"{Gesture(EditorIntent.StepBack)} {Gesture(EditorIntent.StepForward)} step a frame · "
            + $"{Gesture(EditorIntent.Undo)} undoes\n"
            + $"{Gesture(EditorIntent.ToggleHelp)} for all shortcuts";

        HintLabel.Text = $"{Gesture(EditorIntent.ToggleHelp)} shortcuts";
    }

    private string Gesture(EditorIntent intent) => _keys.GestureFor(intent);

    /// <summary>
    /// Gives every toolbar button a tooltip naming it and the key that does the same thing.
    /// </summary>
    /// <remarks>
    /// Built from the button's own automation name, so the thing a screen reader announces
    /// and the thing the tooltip says are one string rather than two that agree today.
    /// </remarks>
    private void BuildToolTips()
    {
        // The transport row is not the toolbar, but its buttons are the same thing wearing
        // a smaller icon, and they are the ones most in need of a tooltip: at 10 pixels a
        // glyph names itself less well than it does at 17.
        foreach (var button in Buttons(Toolbar).Concat(Buttons(TransportControls)))
            button.ToolTip = new ShortcutTip(
                AutomationProperties.GetName(button),
                button.Tag is EditorIntent intent ? Gesture(intent) : "",
                AutomationProperties.GetHelpText(button));
    }

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is Button button) yield return button;

            if (child is DependencyObject node)
                foreach (var nested in Buttons(node))
                    yield return nested;
        }
    }

    private async Task ExportAsync()
    {
        if (!_model.HasMedia)
        {
            StatusLabel.Text = "Nothing to export.";
            return;
        }

        var first = _model.Project.Sources[0];
        var dialog = new SaveFileDialog
        {
            Title = "Save as",
            Filter = "MP4 video|*.mp4",
            FileName = Path.GetFileNameWithoutExtension(first.Path) + "-edit.mp4",
            InitialDirectory = Path.GetDirectoryName(first.Path),
        };

        if (dialog.ShowDialog(this) != true)
        {
            Keyboard.Focus(this);
            return;
        }

        // The save dialog took focus; the shortcuts are useless until it comes back.
        Keyboard.Focus(this);

        _model.FlushSession();

        var temp = Path.Combine(Path.GetTempPath(), "BertCut", Guid.NewGuid().ToString("N"));

        try
        {
            var plan = ExportPlanner.Plan(
                _model.Project,
                new ExportSettings(dialog.FileName),
                _runtime.Capabilities,
                _model.IndexOf,
                temp);

            var mode = plan.Mode == ExportMode.LosslessVideo
                ? "lossless"
                : $"re-encoding ({Describe(plan.Blocker)})";

            StatusLabel.Text = $"Exporting — {mode}...";

            var progress = new Progress<ExportStatus>(s =>
                StatusLabel.Text = $"Exporting — {mode} — {s.Fraction:P0} — {s.Description}");

            await new ExportRunner(_runtime).RunAsync(plan, temp, progress);

            StatusLabel.Text = $"Exported to {dialog.FileName}";
        }
        catch (Exception e) when (e is FfmpegException or InvalidOperationException or IOException)
        {
            StatusLabel.Text = $"Export failed: {e.Message.Split('\n')[0]}";
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    private static string Describe(LosslessBlocker blocker) => blocker switch
    {
        LosslessBlocker.HasCrop => "a crop changes the pixels",
        LosslessBlocker.HasOverlay => "an overlay changes the pixels",
        LosslessBlocker.MultipleSources => "more than one source",
        LosslessBlocker.CutsMissKeyframes => "a cut resumes mid-GOP",
        LosslessBlocker.Disabled => "exact cuts requested",
        _ => "",
    };

    /// <summary>
    /// Copies the composited frame into the WPF bitmap.
    /// </summary>
    /// <remarks>
    /// The whole of the UI thread's per-frame cost, now that the decode happens elsewhere:
    /// one copy out of a buffer the pump has lent us for exactly as long as it is on screen.
    /// It is also the permanent fallback for remote desktop sessions and machines where
    /// hardware compositing is unavailable.
    /// </remarks>
    private void Present(DecodedFrame frame)
    {
        if (_surface is null || _surface.PixelWidth != frame.Width || _surface.PixelHeight != frame.Height)
        {
            _surface = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            PreviewImage.Source = _surface;
            EmptyHint.Visibility = Visibility.Collapsed;
        }

        _surface.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Pixels,
            frame.Stride,
            0);
    }

    /// <summary>
    /// Tells the model how much detail the picture on screen can actually show.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The preview used to be composited at the project's full output size and handed to WPF
    /// to shrink — so a 1280x768 recording displayed in a 700px-wide pane scaled, copied and
    /// uploaded four times the pixels it could show, and then paid for a HighQuality resample
    /// on top. Rendering at the displayed size instead makes each of those a quarter the
    /// work, and the result is sharper: ffmpeg's scaler does the reduction rather than WPF's.
    /// </para>
    /// <para>
    /// <b>Snapped to an integer divisor</b>, and never below what is displayed. Following the
    /// pane exactly would rebuild every decoder and scaler context continuously while a window
    /// edge is being dragged; this way there are four possible sizes and a resize usually
    /// changes nothing at all.
    /// </para>
    /// </remarks>
    private void ApplyPreviewSize()
    {
        var output = _model.Project.Output;
        if (output.Width <= 0 || output.Height <= 0) return;
        if (PreviewPane.ActualWidth <= 0 || PreviewPane.ActualHeight <= 0) return;

        // Stretch="Uniform", so the picture fits the tighter of the two axes.
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = Math.Min(
            PreviewPane.ActualWidth * dpi.DpiScaleX / output.Width,
            PreviewPane.ActualHeight * dpi.DpiScaleY / output.Height);

        if (scale <= 0) return;

        var divisor = Math.Clamp((int)Math.Ceiling(1 / scale), 1, MaxPreviewDivisor);

        _model.SetRenderSize(
            Math.Max(1, output.Width / divisor),
            Math.Max(1, output.Height / divisor));
    }

    /// <summary>
    /// How far below the output size the preview may be composited.
    /// </summary>
    /// <remarks>
    /// A quarter in each axis is a sixteenth of the pixels, which is already past the point
    /// where the saving matters and approaching the point where the picture stops being worth
    /// looking at on a small window.
    /// </remarks>
    private const int MaxPreviewDivisor = 4;

    private static readonly Brush StoppedBrush = Frozen(Color.FromRgb(0xFF, 0x5C, 0x5C));
    private static readonly Brush MovingBrush = Frozen(Color.FromRgb(0x6D, 0xD4, 0x8B));

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        TimecodeLabel.Text = _model.TimecodeText;
        SelectionLabel.Text = _model.SelectionText;
        TransportLabel.Text = _model.TransportText;

        // Whichever of the two is the one worth pressing. Assigned unconditionally: setting
        // Visibility to the value it already holds is free, and this runs every frame while
        // the video is playing.
        PlayButton.Visibility = _model.IsStopped ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = _model.IsStopped ? Visibility.Collapsed : Visibility.Visible;

        TransportIcon.Text = _model.TransportGlyph;
        TransportIcon.Foreground = _model.IsStopped ? StoppedBrush : MovingBrush;

        // The glyph is the whole readout for a screen reader, so it carries the words.
        TransportIcon.ToolTip = _model.TransportText;
        AutomationProperties.SetHelpText(TransportIcon, _model.TransportText);

        // Unlike the transport, this changes only when the key is pressed, so it is worth the
        // property check: swapping a Geometry every frame of playback would be work for
        // nothing.
        if (e.PropertyName is nameof(EditorViewModel.IsMuted)) ApplyMuteGlyph();

        if (e.PropertyName is nameof(EditorViewModel.Status)) StatusLabel.Text = _model.Status;

        if (e.PropertyName is nameof(EditorViewModel.Mode)) SyncOverlaySourceCard();

        // A new video brings its own output format, and the divisor is relative to it. The
        // pane's own size has not changed, so nothing else would ask.
        if (e.PropertyName is nameof(EditorViewModel.Project)) ApplyPreviewSize();
    }

    /// <summary>
    /// Points the mute button at whichever glyph and wording matches the current state.
    /// </summary>
    /// <remarks>
    /// Also called from <see cref="ApplyBindings"/>, because the tooltip prints the key and
    /// the user can move it. Rebuilding the tooltip here rather than leaving it to
    /// <see cref="BuildToolTips"/> is what keeps the name in step with the glyph: the shared
    /// builder reads a fixed automation name, and this button's changes.
    /// </remarks>
    private void ApplyMuteGlyph()
    {
        var muted = _model.IsMuted;
        var label = muted ? "Unmute the preview" : "Mute the preview";

        MuteButton.Content = FindResource(muted ? "Icon.Muted" : "Icon.Sound");
        MuteButton.Foreground = muted ? MutedBrush : UnmutedBrush;

        AutomationProperties.SetName(MuteButton, label);

        MuteButton.ToolTip = new ShortcutTip(
            label,
            Gesture(EditorIntent.ToggleMute),
            "Monitoring only — it does not change the exported file.");
    }

    /// <summary>Warm enough to catch the eye, since silence has no other tell.</summary>
    private static readonly Brush MutedBrush = Frozen(Color.FromRgb(0xE0, 0x8A, 0x55));

    /// <summary>The same grey the rest of the transport chips use.</summary>
    private static readonly Brush UnmutedBrush = Frozen(Color.FromRgb(0x9A, 0xA8, 0xB9));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Fills the shortcut reference from the key map the dispatcher answers to.
    /// </summary>
    /// <remarks>
    /// Generated rather than written out, which is what stops the sheet drifting from the
    /// keys — including away from the user's own changes to them.
    /// </remarks>
    private void BuildHelp() =>
        HelpList.ItemsSource = _keys.ForHelp()
            .Select(group => new HelpGroup(
                group.Key,
                group.Select(b => new { Gesture = GestureText.Format(b), b.Description })))
            .ToList();

    protected override void OnClosing(CancelEventArgs e)
    {
        _model.FlushSession();
        _model.Dispose();
        base.OnClosing(e);
    }
}

/// <summary>A named group of bindings for the shortcut sheet.</summary>
public sealed class HelpGroup(string key, IEnumerable<object> items) : List<object>(items)
{
    public string Key { get; } = key;
}
