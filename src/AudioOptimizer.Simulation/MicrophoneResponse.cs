namespace AudioOptimizer.Simulation;

using System.Numerics;
using AudioOptimizer.Dsp;

/// <summary>
/// The microphone's own frequency response, in the one shape this phase models: a smooth log-frequency tilt between
/// the sweep's band edges (20–150 Hz), with a signed peak deviation at each end. <see cref="Perfect"/> is the flat
/// no-op every existing measurement uses.
/// </summary>
public enum MicrophoneResponseProfile
{
    /// <summary>Flat 0 dB — the no-op profile.</summary>
    Perfect,

    /// <summary>+deviation at 20 Hz, falling to −deviation at 150 Hz.</summary>
    LowFrequencyTilt,

    /// <summary>−deviation at 20 Hz, rising to +deviation at 150 Hz.</summary>
    HighFrequencyTilt,
}

/// <summary>
/// The microphone's response as a function of frequency, and the recording it would have produced for the same sound.
/// The deviation is applied to the RECORDING, after the subs have summed and before the self-noise is added, so the
/// measurement chain carries the error while the ground truth — built from raw impulse responses — never sees it.
/// It is a magnitude-only, zero-phase tilt over the whole capture: it colors the level without moving the arrival.
/// </summary>
public static class MicrophoneResponse
{
    private const double LowHz = 20.0;
    private const double HighHz = 150.0;

    /// <summary>The signed deviation of <paramref name="profile"/> at one frequency, dB, clamped to the band edges.</summary>
    public static double DeviationDb(MicrophoneResponseProfile profile, double deviationDb, double frequencyHz)
    {
        if (profile == MicrophoneResponseProfile.Perfect || deviationDb == 0.0) return 0.0;

        double position = Math.Clamp(
            (Math.Log(frequencyHz) - Math.Log(LowHz)) / (Math.Log(HighHz) - Math.Log(LowHz)), 0.0, 1.0);
        double shape = profile switch
        {
            MicrophoneResponseProfile.LowFrequencyTilt => 1.0 - (2.0 * position),
            MicrophoneResponseProfile.HighFrequencyTilt => (2.0 * position) - 1.0,
            _ => 0.0,
        };

        return deviationDb * shape;
    }

    /// <summary>
    /// The recording this microphone would have made for the same sound. A <see cref="MicrophoneResponseProfile.Perfect"/>
    /// profile or a zero deviation returns the SAME array, so every existing bit-identical path is untouched.
    /// <para>
    /// Applied as one real, even gain per FFT bin of the zero-padded capture, then truncated back to the capture length.
    /// The response belongs to the microphone, so it colors the whole capture; the pre-roll is silence and stays silence.
    /// </para>
    /// </summary>
    public static double[] Apply(double[] recording, int sampleRate, MicrophoneResponseProfile profile, double deviationDb)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (profile == MicrophoneResponseProfile.Perfect || deviationDb == 0.0) return recording;

        int n = Fft.NextPowerOfTwo(recording.Length);
        var spectrum = new Complex[n];
        for (int i = 0; i < recording.Length; i++) spectrum[i] = new Complex(recording[i], 0.0);
        Fft.Forward(spectrum);

        double binHz = (double)sampleRate / n;
        for (int k = 0; k <= n / 2; k++)
        {
            double gain = ComplexMath.DbToLinear(DeviationDb(profile, deviationDb, k * binHz));
            if (gain == 1.0) continue;
            spectrum[k] *= gain;
            if (k > 0 && k < n / 2) spectrum[n - k] *= gain;   // even in f, so the capture stays real
        }

        Fft.Inverse(spectrum);
        var colored = new double[recording.Length];
        for (int i = 0; i < colored.Length; i++) colored[i] = spectrum[i].Real;
        return colored;
    }
}
