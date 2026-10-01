namespace AudioOptimizer.UI.Localization;

using System.Globalization;
using System.Windows.Data;
using AudioOptimizer.Simulation;

/// <summary>Shows a scenario's own title in English, and a plain-language title in Chinese.</summary>
public sealed class ScenarioTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SimulationScenario scenario) return string.Empty;
        string named = UiText.Get("Sim.Title." + scenario.Id);
        return named.StartsWith("Sim.", StringComparison.Ordinal) ? scenario.Title : named;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
