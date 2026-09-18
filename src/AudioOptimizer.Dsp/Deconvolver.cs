namespace AudioOptimizer.Dsp;

/// <summary>
/// Farina deconvolution: the impulse response is the recording convolved with the inverse filter.
/// </summary>
public static class Deconvolver
{
    /// <summary>
    /// IR = recording ⊛ inverseFilter (linear convolution, zero-padded FFT).
    /// The two arrays need not have the same length: a recording that is longer than the sweep
    /// (extra capture tail, trailing silence) is handled by the general-length convolution.
    /// Index convention: a recording whose first sample is the sweep onset puts the zero-lag point at
    /// index (inverseFilter.Length - 1), so a system impulse at delay D appears at that index + D.
    /// </summary>
    public static double[] Deconvolve(double[] recording, double[] inverseFilter)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(inverseFilter);
        if (recording.Length == 0) throw new ArgumentException("Recording is empty.", nameof(recording));
        if (inverseFilter.Length == 0) throw new ArgumentException("Inverse filter is empty.", nameof(inverseFilter));

        return Fft.Convolve(recording, inverseFilter);
    }
}
