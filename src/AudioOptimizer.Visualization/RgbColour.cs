namespace AudioOptimizer.Visualization;

/// <summary>
/// An opaque 8-bit colour. The visualization layer has to name colours for plot series and heatmap cells, but
/// it must not reference <c>System.Windows.Media.Color</c>: the UI converts these to WPF colours at the edge,
/// which is what keeps this assembly headless and OS-independent.
/// </summary>
public readonly record struct RgbColour(byte R, byte G, byte B)
{
    public static RgbColour White { get; } = new(255, 255, 255);

    public static RgbColour Black { get; } = new(0, 0, 0);

    /// <summary>Linear blend, <paramref name="t"/> clamped to [0, 1].</summary>
    public static RgbColour Lerp(RgbColour from, RgbColour to, double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        return new RgbColour(
            (byte)Math.Round(from.R + (to.R - from.R) * clamped),
            (byte)Math.Round(from.G + (to.G - from.G) * clamped),
            (byte)Math.Round(from.B + (to.B - from.B) * clamped));
    }

    /// <summary>"#RRGGBB", the form a test or a log can compare against.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}
