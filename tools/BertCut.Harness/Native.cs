using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace BertCut.Harness;

/// <summary>
/// The few Win32 calls it takes to keep a window off the user's screen and prove it stayed there.
/// </summary>
/// <remarks>
/// Declared with <c>DllImport</c> rather than <c>LibraryImport</c>: the generated marshalling
/// wants unsafe code, and turning that on for a project whose only native calls are four
/// window-management functions buys nothing.
/// </remarks>
internal static class Native
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(nint window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(nint window, out int processId);

    /// <summary>The process that owns the foreground window, or 0 when there is none.</summary>
    public static int ForegroundProcessId()
    {
        var handle = GetForegroundWindow();
        if (handle == 0) return 0;

        GetWindowThreadProcessId(handle, out var processId);
        return processId;
    }

    /// <summary>
    /// Makes a window incapable of being activated, and invisible to Alt-Tab and the taskbar.
    /// </summary>
    /// <remarks>
    /// Belt to <c>ShowActivated = false</c>'s braces. That alone stops the window taking focus
    /// as it opens; this stops anything taking it afterwards, including the window itself
    /// calling <c>SetForegroundWindow</c> — which is what an editor restoring focus to its own
    /// canvas looks like from the outside.
    /// </remarks>
    public static void MakeNonInteractive(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;

        var style = GetWindowLongPtr(handle, GwlExStyle);
        SetWindowLongPtr(handle, GwlExStyle, style | WsExToolWindow | WsExNoActivate);

        // The one that actually holds. WS_EX_NOACTIVATE only stops a *click* activating the
        // window; SetFocus from the owning thread still activates it, and MainWindow focuses
        // itself on Loaded so bare letter keys reach the editor. A disabled window cannot take
        // focus by any route, and since no real keystroke is ever sent here, it costs nothing.
        EnableWindow(handle, false);
    }

    /// <summary>Hands the foreground back to a window.</summary>
    public static void RestoreForeground(nint window)
    {
        if (window != 0) SetForegroundWindow(window);
    }

    /// <summary>The foreground window's handle and title, for proving the user was left alone.</summary>
    public static (nint Handle, string Title) Foreground()
    {
        var handle = GetForegroundWindow();
        if (handle == 0) return (0, "<none>");

        var text = new StringBuilder(512);
        var length = GetWindowText(handle, text, text.Capacity);

        return (handle, length > 0 ? text.ToString() : "<untitled>");
    }

    /// <summary>Formats a foreground reading for the log.</summary>
    public static string Describe(this (nint Handle, string Title) foreground) =>
        $"0x{foreground.Handle:X} \"{foreground.Title}\"";
}
