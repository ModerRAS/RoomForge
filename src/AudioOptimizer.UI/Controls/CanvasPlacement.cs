namespace AudioOptimizer.UI.Controls;

using System.Windows;
using System.Windows.Controls;

/// <summary>Positioning helper, so canvas placement reads at the point of use.</summary>
internal static class CanvasPlacement
{
    public static T Placed<T>(this T element, double left, double top)
        where T : UIElement
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        return element;
    }
}
