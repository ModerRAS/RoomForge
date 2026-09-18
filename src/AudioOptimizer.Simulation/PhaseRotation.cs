namespace AudioOptimizer.Simulation;

using System.Numerics;
using AudioOptimizer.Dsp;

/// <summary>
/// A subwoofer's fixed phase setting, applied where it physically lives: to the DRIVE signal, not to the room.
/// <para>
/// The setting is a frequency-independent rotation exp(jφ), so its real-signal realisation is the Hilbert mix
/// <c>y(t) = cos φ·x(t) + sin φ·x̂(t)</c>. That filter is not causal — its impulse response is cos φ·δ(t) + sin φ/(πt),
/// whose acausal half carries as much energy as the causal one — so the rotation is only realisable when the signal it
/// acts on is BAND-LIMITED, which is exactly what a swept measurement's drive signal is. Rotating a broadband impulse
/// train instead (a room's image-source arrivals) throws away that acausal half and costs tens of percent of the
/// transform's amplitude: measured, 0.22 of the response scale for a single-tap 90° rotation, and 0.14 for an order-3
/// lattice. Applied here, the ring the rotation adds is bounded by 1/(sweep bandwidth) ≈ 7 ms, which is what the pad
/// below is for.
/// </para>
/// <para>
/// The returned array is the input padded by <see cref="PadSamples"/> on both sides, with the input's own first sample
/// at index <c>PadSamples</c> (the ring lives in the padding). The caller places it that many samples earlier, so the
/// rotation adds no delay: an ideal constant-phase filter has zero group delay, and this keeps it that way.
/// </para>
/// </summary>
public static class PhaseRotation
{
    /// <summary>Padding either side of the drive signal: 0.17 s at 48 kHz, room for a 1/(2·20 Hz) = 25 ms ring.</summary>
    public const int PadSamples = 8192;

    /// <summary>
    /// <paramref name="samples"/> rotated by <paramref name="phaseDegrees"/>. <paramref name="offsetSamples"/> is how
    /// much earlier the result must be placed; it is 0 when the rotation is skipped.
    /// </summary>
    public static double[] Apply(double[] samples, double phaseDegrees, out int offsetSamples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!double.IsFinite(phaseDegrees)) throw new ArgumentOutOfRangeException(nameof(phaseDegrees), phaseDegrees, "Phase must be finite.");

        if (phaseDegrees % 360.0 == 0.0)
        {
            offsetSamples = 0;
            return samples;
        }

        var padded = new double[samples.Length + (2 * PadSamples)];
        Array.Copy(samples, 0, padded, PadSamples, samples.Length);
        Rotate(padded, phaseDegrees);
        offsetSamples = PadSamples;
        return padded;
    }

    /// <summary>
    /// y·e^(jφ) for positive frequencies and its conjugate for negative ones. The DC and Nyquist bins are real in a real
    /// signal, so they take the rotation's real part; leaving them alone instead puts a constant offset into the time
    /// domain (measured: 6.1e-5 of a unit drive signal for a 180° rotation that must be an exact sign flip).
    /// </summary>
    private static void Rotate(double[] samples, double phaseDegrees)
    {
        int fftSize = Fft.NextPowerOfTwo(samples.Length + PadSamples);
        var spectrum = new Complex[fftSize];
        for (int i = 0; i < samples.Length; i++) spectrum[i] = new Complex(samples[i], 0.0);
        Fft.Forward(spectrum);

        Complex rotation = Complex.FromPolarCoordinates(1.0, phaseDegrees * Math.PI / 180.0);
        int half = fftSize / 2;
        spectrum[0] *= rotation.Real;
        spectrum[half] *= rotation.Real;
        for (int k = 1; k < half; k++)
        {
            Complex rotated = spectrum[k] * rotation;
            spectrum[k] = rotated;
            spectrum[fftSize - k] = Complex.Conjugate(rotated);   // Hermitian, so the result stays a real signal
        }

        Complex[] timeDomain = Fft.Inverse(spectrum);
        for (int i = 0; i < samples.Length; i++) samples[i] = timeDomain[i].Real;
    }
}
