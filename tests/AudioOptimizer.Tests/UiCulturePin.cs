using System.Runtime.CompilerServices;
using AudioOptimizer.UI.Localization;

namespace AudioOptimizer.Tests;

/// <summary>
/// The product opens in Chinese. Render tests assert the English sentences, so the suite pins English
/// before any view model stores a sentence.
/// </summary>
internal static class UiCulturePin
{
    [ModuleInitializer]
    internal static void PinEnglish() => UiText.Use("en");
}
