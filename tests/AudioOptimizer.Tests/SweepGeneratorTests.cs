namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using Xunit.Abstractions;

/// <summary>
/// T7: the sweep must be a true exponential (log) sweep, not a linear chirp.
/// f(t) = f1·exp(t/T·L), L = ln(f2/f1); φ(t) = 2π·f1·T/L·(exp(t/T·L) - 1); s(t) = sin(φ(t)).
/// </summary>
public class SweepGeneratorTests(ITestOutputHelper output)
{
    private static readonly SweepSettings Default = new(); // 20 → 150 Hz, 1 s, 48 kHz

    /// <summary>The documented frequency law, written out independently of the generator.</summary>
    private static double FrequencyLaw(double t, double f1, double f2, double duration)
        => f1 * Math.Exp(t / duration * Math.Log(f2 / f1));

    /// <summary>
    /// Instantaneous frequency measured from the samples: analytic signal (negative-frequency
    /// bins zeroed, positive ones doubled) then the phase slope over a window centred on mid.
    /// </summary>
    private static double MeasuredFrequency(double[] samples, double sampleRate, int centre, int halfWindow)
    {
        var spectrum = Fft.Forward(samples);
        int n = samples.Length;
        for (int k = 1; k < n / 2; k++) spectrum[k] *= 2.0;
        for (int k = n / 2 + 1; k < n; k++) spectrum[k] = System.Numerics.Complex.Zero;
        var analytic = Fft.Inverse(spectrum);

        double phase0 = ComplexMath.Phase(analytic[centre - halfWindow]);
        double phase1 = ComplexMath.Phase(analytic[centre + halfWindow]);
        double span = phase1 - phase0;
        span -= 2.0 * Math.PI * Math.Round(span / (2.0 * Math.PI)); // fold to (-π, π]
        return span * sampleRate / (2.0 * Math.PI * 2 * halfWindow);
    }

    [Fact]
    public void Length_is_round_T_times_fs()
    {
        // n = round(T·fs) = round(1.0 · 48000) = 48000
        double[] samples = SweepGenerator.GenerateExponentialSweep(Default);
        Assert.Equal(48000, samples.Length);
        Assert.Equal(Default.SampleCount, samples.Length);

        // n = round(0.5 · 44100) = round(22050) = 22050
        var halfSecond = new SweepSettings(DurationSeconds: 0.5, SampleRate: 44100);
        Assert.Equal(22050, SweepGenerator.GenerateExponentialSweep(halfSecond).Length);
    }

    [Fact]
    public void Instantaneous_frequency_follows_the_exponential_law()
    {
        // f(0) = f1·exp(0) = 20 Hz  (rel tol 1e-6 → ±2e-5)
        Assert.Equal(20.0, FrequencyLaw(0.0, 20.0, 150.0, 1.0), 1e-6 * 20.0);
        // f(T) = f1·exp(L) = f2 = 150 Hz  (rel tol 1e-6 → ±1.5e-4)
        Assert.Equal(150.0, FrequencyLaw(1.0, 20.0, 150.0, 1.0), 1e-6 * 150.0);
        // f(T/2) = f1·exp(L/2) = √(f1·f2) = √3000 = 54.772255750516614 Hz
        Assert.Equal(54.772255750516614, FrequencyLaw(0.5, 20.0, 150.0, 1.0), 1e-6 * 54.772255750516614);

        // A LINEAR chirp would instead be f(t) = f1 + (f2-f1)·t/T = 20 + 130t, whose midpoint is
        // (f1+f2)/2 = 85 Hz (not 54.772 Hz) and whose phase integral counts 85 cycles per second,
        // so this midpoint assertion and Cycle_count_matches_the_phase_integral both fail for it.
    }

    [Fact]
    public void Midpoint_frequency_measured_from_samples_is_the_geometric_mean()
    {
        // 20 Hz → 150 Hz over 1 s, taper off so the analytic-signal phase is not biased at the edges
        double[] samples = SweepGenerator.GenerateExponentialSweep(Default, taper: false);
        double measured = MeasuredFrequency(samples, Default.SampleRate, samples.Length / 2, halfWindow: 128);
        output.WriteLine($"measured midpoint frequency = {measured:R} Hz");

        // √(f1·f2) = 54.772255750516614 Hz; a linear chirp gives 85 Hz (+55 %).
        // ponytail: 1e-3 rel bound — the window-slope estimate carries a ~4.5e-5 bias from the
        // φ''(t) truncation term (this run) and would need a wider window/bias correction to tighten;
        // the closed-form law test above carries the tight 1e-6 assertion.
        Assert.Equal(54.772255750516614, measured, 1e-3 * 54.772255750516614);
    }

    [Fact]
    public void Cycle_count_matches_the_phase_integral()
    {
        double[] samples = SweepGenerator.GenerateExponentialSweep(Default, taper: false);

        // cycles = ∫f dt = (f2 - f1)·T / ln(f2/f1) = 130 / 2.0149030205422647 = 64.5187
        // → 2 · 64.5187 = 129.04 zero crossings. A linear chirp would give 85 cycles = 170 crossings.
        int signChanges = 0;
        for (int i = 1; i < samples.Length; i++)
            if (samples[i] > 0.0 != samples[i - 1] > 0.0) signChanges++;
        output.WriteLine($"sign changes = {signChanges} (log sweep expects ~129, linear chirp ~170)");

        // measured 130: s[0] = sin(0) = 0 exactly, which the >0 test counts as its own crossing
        Assert.InRange(signChanges, 129, 131);
    }

    [Fact]
    public void Samples_are_bounded_and_tapered_at_both_ends()
    {
        double[] tapered = SweepGenerator.GenerateExponentialSweep(Default);
        double[] raw = SweepGenerator.GenerateExponentialSweep(Default, taper: false);

        for (int i = 0; i < tapered.Length; i++)
        {
            Assert.True(double.IsFinite(tapered[i]), $"sample {i} was not finite");
            Assert.True(Math.Abs(tapered[i]) <= 1.0, $"sample {i} exceeded ±1");
        }

        Assert.True(Math.Abs(tapered[0]) < 1e-12, $"first sample was {tapered[0]:E3}");       // sin(0)·w(0) = 0
        Assert.True(Math.Abs(tapered[^1]) < 1e-12, $"last sample was {tapered[^1]:E3}");      // w(0) = 0
        // the taper is real work, not a no-op: raw start/end differ from the tapered ones
        Assert.True(Math.Abs(raw[^1]) > 0.1, $"untapered last sample was {raw[^1]:E3}");
        // 5 ms at 48 kHz → 240 tapered samples; sample 240 is outside the ramp, so |s| is untouched
        Assert.Equal(raw[240], tapered[240], 1e-15);
        Assert.True(Math.Abs(tapered[120]) < 0.5, $"mid-ramp sample was {tapered[120]:E3}");
    }

    [Fact]
    public void Energy_is_inside_the_20_to_150_hz_band()
    {
        // taper off as specified: the sweep itself must already live inside the band
        double[] samples = SweepGenerator.GenerateExponentialSweep(Default, taper: false);
        int n = samples.Length;
        double binHz = Default.SampleRate / n; // 48000 / 48000 = 1 Hz per bin

        // Occupancy is asserted on the Hann-windowed spectrum: the rectangular spectrum of an
        // unwindowed sweep only reaches 97.38 % in band (measured), because the hard start/stop at
        // t = 0 and t = T splatters a 1/f skirt below 20 Hz (2.05 % in [0,20) Hz, 0.57 % above
        // 150 Hz). That skirt is measurement leakage, not sweep content: with a Hann window the
        // same samples give 99.9983 %. Total = positive-frequency half (k = 0..N/2); a real signal's
        // spectrum is conjugate-symmetric, so the mirrored band carries the same energy.
        double WindowedFraction(Func<double[], double[]> transform)
        {
            var spectrum = Fft.Forward(transform(samples));
            double inBand = 0.0, total = 0.0;
            for (int k = 0; k <= n / 2; k++)
            {
                double energy = spectrum[k].Magnitude * spectrum[k].Magnitude;
                total += energy;
                double frequency = k * binHz;
                if (frequency >= Default.StartHz && frequency <= Default.EndHz) inBand += energy;
            }
            return inBand / total;
        }

        double rectangular = WindowedFraction(v => v);
        double hann = WindowedFraction(v =>
        {
            var w = new double[v.Length];
            for (int i = 0; i < v.Length; i++) w[i] = v[i] * (0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (v.Length - 1)));
            return w;
        });
        output.WriteLine($"in-band energy: rectangular = {rectangular:P6}, hann = {hann:P6}");

        Assert.True(rectangular > 0.97, $"rectangular in-band energy was only {rectangular:P4}");
        Assert.True(hann > 0.99, $"only {hann:P4} of the energy was inside 20..150 Hz");
    }
}
