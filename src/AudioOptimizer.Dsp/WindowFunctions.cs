namespace AudioOptimizer.Dsp;

using AudioOptimizer.Core;

/// <summary>
/// Analysis-window shapes. Hann and Tukey use the <b>periodic</b> form (denominator <c>length</c>,
/// not <c>length - 1</c>): for FFT analysis the window is implicitly repeated every frame, so the
/// periodic sample sequence w[0] … w[N-1] continues into the next frame without a step. The
/// symmetric form is meant for filter design and biases the coherent gain (measured periodic Hann
/// mean = 0.5 exactly, symmetric Hann mean = (N-1)/2N ≈ 0.4999).
/// </summary>
public static class WindowFunctions
{
    /// <param name="tukeyAlpha">Taper fraction per side: 0 = rectangular, 1 = Hann-shaped.</param>
    public static double[] Create(WindowType type, int length, double tukeyAlpha = 0.5)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), length, "Window length must be > 0.");

        var window = new double[length];
        switch (type)
        {
            case WindowType.Rectangular:
                Array.Fill(window, 1.0);
                break;

            case WindowType.Hann:
                // periodic: w[n] = 0.5 - 0.5·cos(2πn/N)  →  mean = 0.5 exactly
                for (int n = 0; n < length; n++) window[n] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / length);
                break;

            case WindowType.Tukey:
                if (tukeyAlpha < 0.0 || tukeyAlpha > 1.0)
                    throw new ArgumentOutOfRangeException(nameof(tukeyAlpha), tukeyAlpha, "tukeyAlpha must be in [0, 1].");
                int taper = (int)Math.Floor(tukeyAlpha * length / 2.0);
                for (int n = 0; n < length; n++)
                {
                    if (taper == 0) window[n] = 1.0;
                    else if (n < taper) window[n] = 0.5 * (1.0 - Math.Cos(Math.PI * n / taper));
                    else if (n >= length - taper) window[n] = 0.5 * (1.0 - Math.Cos(Math.PI * (length - n) / taper));
                    else window[n] = 1.0;
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown window type.");
        }
        return window;
    }

    /// <summary>Multiplies <paramref name="samples"/> by the window in place.</summary>
    public static void ApplyInPlace(double[] samples, WindowType type, double tukeyAlpha = 0.5)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double[] window = Create(type, samples.Length, tukeyAlpha);
        for (int i = 0; i < samples.Length; i++) samples[i] *= window[i];
    }
}
