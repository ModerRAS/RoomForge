namespace AudioOptimizer.Core;

/// <summary>Analysis window shape used when converting an impulse response into a frequency response.</summary>
public enum WindowType
{
    Rectangular,
    Hann,
    Tukey,
}
