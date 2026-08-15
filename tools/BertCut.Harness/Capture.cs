using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BertCut.Harness;

/// <summary>
/// Turns a piece of the live visual tree into a PNG.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RenderTargetBitmap"/> walks the visual tree and rasterises it in software. It
/// is not a screen grab, which is the entire reason this works: the window it is capturing
/// has never been presented, sits outside every monitor, and could be covered by anything at
/// all, and the picture is identical either way.
/// </para>
/// <para>
/// Captured at 96 DPI against the element's own device-independent size, so the file is the
/// same on a scaled monitor as on an unscaled one. <c>Render</c> applies the visual's own
/// transforms but not the window's device transform, so no scale correction belongs here.
/// </para>
/// </remarks>
internal static class Capture
{
    public static (int Width, int Height) Save(FrameworkElement element, string path)
    {
        var (width, height) = RenderSize(element);

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException(
                $"{Describe(element)} has no size to capture ({width}x{height}); it is probably collapsed.");

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(AtOrigin(element, width, height));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        encoder.Save(stream);

        return (width, height);
    }

    /// <summary>
    /// Re-hosts an element at the origin so its picture is not shifted by where it sits.
    /// </summary>
    /// <remarks>
    /// <see cref="RenderTargetBitmap.Render"/> applies the visual's offset within its parent,
    /// so rendering a child straight into a bitmap of that child's size draws it at its
    /// window coordinates — the timeline strip, six hundred pixels down, lands entirely
    /// outside the bitmap and comes back blank, and everything else comes back with a margin
    /// on two sides and its far edges cropped. Painting it through a
    /// <see cref="VisualBrush"/> normalises that away; the brush samples the live visual, so
    /// this is still a software re-render of the real tree rather than a copy of anything.
    /// </remarks>
    private static Visual AtOrigin(FrameworkElement element, int width, int height)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null,
                new Rect(0, 0, width, height));
        }

        return visual;
    }

    /// <summary>
    /// How big the picture of an element should be.
    /// </summary>
    /// <remarks>
    /// A window's <c>ActualHeight</c> is its outer height, title bar and borders included,
    /// but its visual tree begins at the client origin — so measuring the window and
    /// rendering it leaves a blank strip along the bottom the width of the chrome. The
    /// content element's size is the client area, which is exactly what gets drawn.
    /// </remarks>
    private static (int Width, int Height) RenderSize(FrameworkElement element)
    {
        var measured = element is Window { Content: FrameworkElement content } ? content : element;

        // The content can measure larger than the client area it was arranged into — the root
        // panel reports what it asked for. RenderSize is what was actually given.
        var size = measured.RenderSize;

        return ((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
    }

    /// <summary>
    /// True when a capture contains more than one colour.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is silent: an offscreen window that rendered nothing
    /// produces a perfectly valid PNG of a single flat colour, and every assertion downstream
    /// would pass while the pictures showed nothing at all.
    /// </remarks>
    public static bool HasContent(string path)
    {
        using var stream = File.OpenRead(path);
        var decoded = new PngBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];

        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var first = BitConverter.ToUInt32(pixels, 0);
        for (var i = 4; i < pixels.Length; i += 4)
            if (BitConverter.ToUInt32(pixels, i) != first)
                return true;

        return false;
    }

    private static string Describe(FrameworkElement element) =>
        string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name;
}
