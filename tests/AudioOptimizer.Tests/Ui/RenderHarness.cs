namespace AudioOptimizer.Tests.Ui;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioOptimizer.Visualization;

/// <summary>
/// One rendered frame: the pixels, the encoded PNG, and the text found in the visual tree. Pixels and text are
/// copied out inside the render thread, so a test can assert on them from the MTA test thread without touching
/// a thread-affine WPF object.
/// </summary>
internal sealed record RenderedImage(int Width, int Height, int Dpi, byte[] Bgra, byte[] Png, IReadOnlyList<string> Texts)
{
    public int Stride => Width * 4;

    /// <summary>Channels at a pixel, decoded from the BGRA32 buffer the render target produced.</summary>
    public (byte B, byte G, byte R, byte A) PixelAt(int x, int y)
    {
        int offset = y * Stride + x * 4;
        return (Bgra[offset], Bgra[offset + 1], Bgra[offset + 2], Bgra[offset + 3]);
    }

    /// <summary>White is the harness background, so "ink" is any channel more than <paramref name="threshold"/> from 255.</summary>
    public int InkPixels(int threshold = 8)
    {
        int ink = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                (byte b, byte g, byte r, _) = PixelAt(x, y);
                if (255 - Math.Min(b, Math.Min(g, r)) > threshold) ink++;
            }

        return ink;
    }

    /// <summary>Pixels within <paramref name="tolerance"/> of a colour — the negative twin for a hidden label.</summary>
    public int CountColour(RgbColour colour, int tolerance = 24)
    {
        int count = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                (byte b, byte g, byte r, _) = PixelAt(x, y);
                if (Math.Abs(r - colour.R) <= tolerance && Math.Abs(g - colour.G) <= tolerance && Math.Abs(b - colour.B) <= tolerance)
                    count++;
            }

        return count;
    }

    /// <summary>
    /// True when the colour appears inside a square of half-width <paramref name="radius"/> around the pixel.
    /// The neighbourhood absorbs the half-pixel convention of a one-pixel aliased stroke; it does not absorb a
    /// wrong mapping, because the probe coordinate is derived independently of the production transform.
    /// </summary>
    public bool HasColourNear(int x, int y, int radius, RgbColour colour, int tolerance = 24)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx;
                int py = y + dy;
                if (px < 0 || py < 0 || px >= Width || py >= Height) continue;
                (byte b, byte g, byte r, _) = PixelAt(px, py);
                if (Math.Abs(r - colour.R) <= tolerance && Math.Abs(g - colour.G) <= tolerance && Math.Abs(b - colour.B) <= tolerance)
                    return true;
            }

        return false;
    }

    /// <summary>The negative twin of <see cref="HasColourNear"/>: nothing was painted here.</summary>
    public bool IsBlankNear(int x, int y, int radius, int threshold = 8)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx;
                int py = y + dy;
                if (px < 0 || py < 0 || px >= Width || py >= Height) continue;
                (byte b, byte g, byte r, _) = PixelAt(px, py);
                if (255 - Math.Min(b, Math.Min(g, r)) > threshold) return false;
            }

        return true;
    }

    /// <summary>
    /// Writes the PNG under the system temp directory. Never into the tree: a UI test that renders images must
    /// not leave artifacts one <c>git add -A</c> away from being committed.
    /// </summary>
    public string WritePng(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), $"roomforge-render-{name}.png");
        File.WriteAllBytes(path, Png);
        return path;
    }
}

/// <summary>
/// The M0 capture mechanism, ratified before this phase: render a visual tree off-screen with
/// <see cref="RenderTargetBitmap"/> → PNG, on a dedicated STA thread with a dispatcher, at a pinned DPI and
/// size. xUnit bodies run on the MTA, so a naive render in a test would misbehave; the factory runs on the STA
/// thread so the elements are created on the thread that renders them (WPF objects are thread-affine).
/// </summary>
internal static class RenderHarness
{
    public const int Dpi = 96;

    /// <summary>Renders one frame of a fresh element created by <paramref name="factory"/>.</summary>
    public static RenderedImage Render(Func<FrameworkElement> factory, double width, double height)
        => OnStaThread(() => Capture(factory(), width, height));

    /// <summary>
    /// Two independent renders inside ONE STA thread — the determinism check. Same thread, same dispatcher,
    /// same run: anything that differs is leakage from theme/DPI/state, not thread scheduling.
    /// </summary>
    public static (RenderedImage First, RenderedImage Second) RenderTwice(Func<FrameworkElement> factory, double width, double height)
        => OnStaThread(() => (Capture(factory(), width, height), Capture(factory(), width, height)));

    private static RenderedImage Capture(FrameworkElement content, double width, double height)
    {
        // Measure + Arrange at the pinned size first: an element that was never laid out renders blank, and a
        // blank bitmap here would look like a GPU problem rather than a missing layout pass.
        content.Width = width;
        content.Height = height;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();

        int pixelWidth = (int)Math.Round(width * Dpi / 96.0);
        int pixelHeight = (int)Math.Round(height * Dpi / 96.0);
        // Pbgra32, not Bgra32: RenderTargetBitmap only accepts the premultiplied formats. The figure is fully
        // opaque (a white background is always painted), so premultiplied and straight values are identical here.
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        bitmap.Freeze();

        int stride = pixelWidth * 4;
        var pixels = new byte[stride * pixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        return new RenderedImage(pixelWidth, pixelHeight, Dpi, pixels, EncodePng(bitmap), CollectText(content));
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Text the user can actually SEE — the presence/absence side of the acceptance rules.
    /// <para>
    /// Walking the tree alone is not enough: a <c>Collapsed</c> TextBlock is still a child, so a raw tree walk
    /// reports a hidden label as "present" and the §23 absence twin would pass (or fail) for the wrong reason.
    /// The predicate is <c>Visibility == Visible</c>, which excludes Collapsed and Hidden. Measured, not
    /// assumed: <see cref="UIElement.IsVisible"/> is FALSE for every element in this off-screen harness
    /// (it needs a PresentationSource, i.e. a real window), so using it would report "no text at all" and make
    /// the presence twin vacuous. The bitmap probe below is the rendering-level half of the same claim.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> CollectText(DependencyObject root)
    {
        var texts = new List<string>();
        Walk(root);
        return texts;

        void Walk(DependencyObject node)
        {
            if (node is TextBlock { Visibility: Visibility.Visible } text && !string.IsNullOrWhiteSpace(text.Text))
                texts.Add(text.Text);
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>
    /// Runs an async body on a dedicated STA thread WITH a dispatcher pump, and returns the thread id it ran on.
    /// The pump is required here, unlike the render path: an awaited continuation is posted to the dispatcher's
    /// synchronisation context and would never run without one. No timing is involved — the body decides when the
    /// blocked work is allowed to finish, so "the call returned while the capture was still blocked" is a
    /// structural fact rather than a stopwatch reading.
    /// </summary>
    public static int RunPumped(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        int threadId = 0;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            // The real application gets a DispatcherSynchronizationContext from Application.Run. Without installing
            // one here, an awaited continuation would resume on a pool thread and this harness would be testing a
            // different world than the app runs in — measured: property notifications arrived on the pool thread
            // until this line existed.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            threadId = Environment.CurrentManagedThreadId;
            try
            {
                Task task = body();
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => dispatcher.Invoke(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();       // surfaces an assertion failure from the body
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return threadId;
    }

    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // Touching CurrentDispatcher initialises the WPF stack on this thread. No message pump is
                // required for an off-screen render — confirmed by this test running green with none.
                _ = Dispatcher.CurrentDispatcher;
                result = work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}
