namespace AudioOptimizer.UI;

using System.Windows.Media;
using AudioOptimizer.Visualization;

/// <summary>
/// The edge where <see cref="RgbColour"/> becomes a WPF colour. Kept in one place so the visualization layer
/// never has to reference <c>System.Windows.Media</c> and the UI never invents its own palette. Brushes are
/// frozen: a figure redraws many times and an unfrozen brush is a per-frame allocation.
/// </summary>
internal static class VisualColours
{
    private static readonly Dictionary<RgbColour, SolidColorBrush> BrushCache = [];

    public static Color ToColor(RgbColour colour) => Color.FromRgb(colour.R, colour.G, colour.B);

    public static SolidColorBrush ToBrush(RgbColour colour)
    {
        if (!BrushCache.TryGetValue(colour, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(ToColor(colour));
            brush.Freeze();
            BrushCache[colour] = brush;
        }

        return brush;
    }
}
