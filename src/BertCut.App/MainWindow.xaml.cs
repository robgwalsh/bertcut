using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BertCut.Core.Export;
using BertCut.Core.Input;
using BertCut.Media;
using Microsoft.Win32;

namespace BertCut.App;

public partial class MainWindow : Window
{
    private readonly FfmpegRuntime _runtime;
    private readonly EditorViewModel _model;
    private WriteableBitmap? _surface;

    public MainWindow(FfmpegRuntime runtime)
    {
        InitializeComponent();

        _runtime = runtime;
        _model = new EditorViewModel(runtime);
        _model.PropertyChanged += OnModelChanged;
        _model.FrameChanged += Present;

        Timeline.Bind(_model);
        Placement.Bind(_model);
        BuildHelp();

        // Playback advances on the composition tick so frames land in step with WPF's
        // own rendering rather than racing it from a timer.
        CompositionTarget.Rendering += (_, _) => _model.Tick();

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

        if (HelpOverlay.Visibility == Visibility.Visible
            && e.Key is Key.Escape or Key.F1)
        {
            HideHelp();
            e.Handled = true;
            return;
        }

        var key = Translate(e.Key);
        if (key == EditorKey.None) return;

        var intent = KeyMap.Resolve(key, CurrentModifiers(), _model.Mode);
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

        switch (intent)
        {
            case EditorIntent.OpenFile: OpenFile(); break;
            case EditorIntent.ImportSource: ImportFile(); break;
            case EditorIntent.AppendSource: AppendFile(); break;
            case EditorIntent.Export: _ = ExportAsync(); break;
            case EditorIntent.ToggleHelp: ToggleHelp(); break;
            default: _model.Dispatch(intent); break;
        }
    }

    /// <summary>Stops the run that holding a frame-step key started.</summary>
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);

        if (Translate(e.Key) is not (EditorKey.Comma or EditorKey.Period)) return;
        if (_model.Mode != EditorMode.Normal) return;

        // Harmless after a tap, which left the transport stopped anyway.
        _model.ReleaseShuttle();
        e.Handled = true;
    }

    private static EditorModifiers CurrentModifiers()
    {
        var modifiers = EditorModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= EditorModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= EditorModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= EditorModifiers.Alt;
        return modifiers;
    }

    /// <summary>Maps WPF keys onto Core's framework-independent key enum.</summary>
    private static EditorKey Translate(Key key) => key switch
    {
        Key.Space => EditorKey.Space,
        Key.Left => EditorKey.Left,
        Key.Right => EditorKey.Right,
        Key.Up => EditorKey.Up,
        Key.Down => EditorKey.Down,
        Key.Home => EditorKey.Home,
        Key.End => EditorKey.End,
        Key.Enter => EditorKey.Enter,
        Key.Escape => EditorKey.Escape,
        Key.Delete => EditorKey.Delete,
        Key.A => EditorKey.A,
        Key.C => EditorKey.C,
        Key.E => EditorKey.E,
        Key.I => EditorKey.I,
        Key.J => EditorKey.J,
        Key.K => EditorKey.K,
        Key.L => EditorKey.L,
        Key.M => EditorKey.M,
        Key.O => EditorKey.O,
        Key.P => EditorKey.P,
        Key.S => EditorKey.S,
        Key.V => EditorKey.V,
        Key.X => EditorKey.X,
        Key.Y => EditorKey.Y,
        Key.Z => EditorKey.Z,
        Key.D1 or Key.NumPad1 => EditorKey.D1,
        Key.D2 or Key.NumPad2 => EditorKey.D2,
        Key.D3 or Key.NumPad3 => EditorKey.D3,
        Key.D4 or Key.NumPad4 => EditorKey.D4,
        Key.D5 or Key.NumPad5 => EditorKey.D5,
        Key.OemMinus or Key.Subtract => EditorKey.Minus,
        Key.OemPlus or Key.Add => EditorKey.Equals,
        Key.OemBackslash or Key.Oem5 => EditorKey.Backslash,
        Key.OemComma => EditorKey.Comma,
        Key.OemPeriod => EditorKey.Period,
        Key.F1 => EditorKey.F1,
        _ => EditorKey.None,
    };

    /// <summary>Opens a file directly, e.g. one passed on the command line.</summary>
    public void OpenPath(string path) => _ = _model.OpenAsync(path);

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a video",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v|All files|*.*",
        };

        if (dialog.ShowDialog(this) == true) _ = _model.OpenAsync(dialog.FileName);
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

    private void AppendFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add a video to the end of the timeline",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v|All files|*.*",
        };

        if (dialog.ShowDialog(this) == true) _ = _model.AppendAsync(dialog.FileName);
    }

    // ---- toolbar ---------------------------------------------------------------------

    private void OnAddSegmentClick(object sender, RoutedEventArgs e) => AppendFile();

    private void OnExportClick(object sender, RoutedEventArgs e) => _ = ExportAsync();

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

    private void OnHelpClick(object sender, RoutedEventArgs e) => ToggleHelp();

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
            Title = "Export",
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
    /// The software preview path. At the resolutions this editor targets a frame is a few
    /// megabytes and this costs about a millisecond, which is well inside a 60 Hz budget.
    /// It is also the permanent fallback for remote desktop sessions and machines where
    /// hardware compositing is unavailable.
    /// </remarks>
    private void Present()
    {
        var preview = _model.Preview;
        if (preview is null || !preview.HasFrame) return;

        var frame = preview.Canvas;

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

    private static readonly Brush StoppedBrush = Frozen(Color.FromRgb(0xFF, 0x5C, 0x5C));
    private static readonly Brush MovingBrush = Frozen(Color.FromRgb(0x6D, 0xD4, 0x8B));

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        TimecodeLabel.Text = _model.TimecodeText;
        SelectionLabel.Text = _model.SelectionText;
        TransportLabel.Text = _model.TransportText;

        TransportIcon.Text = _model.TransportGlyph;
        TransportIcon.Foreground = _model.IsStopped ? StoppedBrush : MovingBrush;

        // The glyph is the whole readout for a screen reader, so it carries the words.
        TransportIcon.ToolTip = _model.TransportText;
        AutomationProperties.SetHelpText(TransportIcon, _model.TransportText);

        if (e.PropertyName is nameof(EditorViewModel.Status)) StatusLabel.Text = _model.Status;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void BuildHelp() =>
        HelpList.ItemsSource = KeyMap.ForHelp()
            .Select(group => group.Select(b => new
            {
                Gesture = Describe(b),
                b.Description,
            }).ToList())
            .Zip(KeyMap.ForHelp().Select(g => g.Key), (items, key) => new HelpGroup(key, items))
            .ToList();

    private static string Describe(Core.Input.KeyBinding binding)
    {
        var parts = new List<string>();
        if (binding.Modifiers.HasFlag(EditorModifiers.Control)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(EditorModifiers.Shift)) parts.Add("Shift");
        if (binding.Modifiers.HasFlag(EditorModifiers.Alt)) parts.Add("Alt");

        parts.Add(binding.Key switch
        {
            EditorKey.Left => "←",
            EditorKey.Right => "→",
            EditorKey.Up => "↑",
            EditorKey.Down => "↓",
            EditorKey.Equals => "=",
            EditorKey.Minus => "-",
            EditorKey.Backslash => "\\",

            // Bound shift-agnostically, so the sheet shows the character people reach for.
            EditorKey.Comma => "<",
            EditorKey.Period => ">",
            var k => k.ToString(),
        });

        return string.Join(" + ", parts);
    }

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
