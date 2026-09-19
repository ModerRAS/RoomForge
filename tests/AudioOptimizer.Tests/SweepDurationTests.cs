namespace AudioOptimizer.Tests;

using System.Collections.Concurrent;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.Measurement;
using AudioOptimizer.Simulation;
using Xunit.Abstractions;

/// <summary>
/// Explicit duration coverage for the shipped ESS construction: 0.5 / 1.0 / 2.0 / 4.0 s at 48 kHz over
/// 20–150 Hz. 2.0 s was the unexercised gap between the 1 s default and the 4 s characterisation sweeps.
///
/// Per duration the file proves the three things the 1 s tests prove:
///   * n = round(T·fs) and the generator returns exactly that many samples;
///   * the samples carry the exponential law (closed form + measured midpoint + phase integral);
///   * excitation ⊛ inverse peaks at the zero-lag sample (N−1) within ±1, with no NaN/Inf.
/// <see cref="Reconstruction_is_consistent_across_the_four_durations"/> then compares peak position,
/// peak amplitude and in-band ripple across the four durations against a pinned measured table and the
/// duration-free ideal band-limited impulse, so a duration-dependent regression (a stale buffer, an
/// envelope built for the wrong T) cannot hide behind a per-duration self-comparison.
///
/// Expectations are REGRESSION PINS in the style of InverseFilterTests / EssEdgeMarginTests: the ESS
/// reconstruction carries a characterised ~12 dB edge artifact and a duration-dependent interior ripple,
/// so flatness is pinned at the measured values with a margin, never asserted at a level the construction
/// cannot reach. The reconstruction is computed once per duration (cached): four 48 kHz sweeps up to 4 s
/// (384k-sample IRs) are affordable, but recomputing them per assertion is not.
/// </summary>
public class SweepDurationTests(ITestOutputHelper output)
{
    private const int Fs = 48000;
    private const double StartHz = 20.0;
    private const double EndHz = 150.0;
    private const double GeometricMeanHz = 54.772255750516614;   // √(f1·f2) = √3000, the midpoint frequency

    private static SweepSettings Settings(double duration) => new(StartHz, EndHz, duration, Fs);

    /// <summary>The documented frequency law, written out independently of the generator (SweepGeneratorTests' helper).</summary>
    private static double FrequencyLaw(double t, double f1, double f2, double duration)
        => f1 * Math.Exp(t / duration * Math.Log(f2 / f1));

    /// <summary>Instantaneous frequency from the samples: analytic signal, then the phase slope around the centre.</summary>
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
        span -= 2.0 * Math.PI * Math.Round(span / (2.0 * Math.PI));
        return span * sampleRate / (2.0 * Math.PI * 2 * halfWindow);
    }

    private static int PeakIndex(double[] samples)
    {
        int peak = 0;
        double best = 0.0;
        for (int i = 0; i < samples.Length; i++)
            if (Math.Abs(samples[i]) > best) { best = Math.Abs(samples[i]); peak = i; }
        return peak;
    }

    /// <summary>Worst |dB − in-band mean| over [lo, hi] — the same statistic InverseFilterTests/EssEdgeMarginTests use.</summary>
    private static double Ripple(FrequencyResponse[] response, double lo, double hi)
    {
        var bins = response.Where(b => b.FrequencyHz >= lo && b.FrequencyHz <= hi).ToArray();
        double mean = bins.Average(b => b.MagnitudeDb);
        return bins.Max(b => Math.Abs(b.MagnitudeDb - mean));
    }

    /// <summary>Least-squares dB-vs-log10(f) slope scaled to the 20→150 Hz span (0 dB for a correct ESS weighting).</summary>
    private static double Tilt(FrequencyResponse[] response, double lo, double hi)
    {
        var bins = response.Where(b => b.FrequencyHz >= lo && b.FrequencyHz <= hi).ToArray();
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (FrequencyResponse bin in bins)
        {
            double x = Math.Log10(bin.FrequencyHz);
            sx += x; sy += bin.MagnitudeDb; sxx += x * x; sxy += x * bin.MagnitudeDb;
        }
        int n = bins.Length;
        return (n * sxy - sx * sy) / (n * sxx - sx * sx) * Math.Log10(hi / lo);
    }

    // ---- one reconstruction per duration, shared by the theory and the cross-duration comparison ----

    private sealed record Reconstruction(
        double DurationSeconds,
        int SampleCount,
        int InverseLength,
        int ImpulseResponseLength,
        int PeakIndex,
        double PeakMagnitude,
        bool AllFinite,
        double BandMeanDb,
        double InteriorRippleDb,   // 30–140 Hz: the band where the ESS edge artifact is small
        double FullRippleDb,       // 20–150 Hz: edge-dominated, ~12 dB by construction
        double TiltDb);

    private static readonly ConcurrentDictionary<double, Reconstruction> Cache = new();

    private static Reconstruction Reconstruct(double duration) => Cache.GetOrAdd(duration, Compute);

    private static Reconstruction Compute(double duration)
    {
        SweepSettings settings = Settings(duration);
        double[] excitation = SweepGenerator.GenerateExponentialSweep(settings);
        double[] inverse = InverseFilter.BuildExponentialInverseSweep(settings);
        double[] impulseResponse = Deconvolver.Deconvolve(excitation, inverse);

        bool allFinite = true;
        for (int i = 0; i < impulseResponse.Length; i++)
            if (!double.IsFinite(impulseResponse[i])) { allFinite = false; break; }

        int peakIndex = PeakIndex(impulseResponse);
        FrequencyResponse[] response = FrequencyResponseCalculator.Compute(
            new ImpulseResponse(impulseResponse, Fs), WindowType.Rectangular);

        // normalisation identity: the inverse filter divides by this mean, so the unity-gain band mean is 1.0 (0 dB)
        double bandMean = response
            .Where(b => b.FrequencyHz >= StartHz && b.FrequencyHz <= EndHz)
            .Average(b => ComplexMath.DbToLinear(b.MagnitudeDb));

        return new Reconstruction(
            duration, settings.SampleCount, inverse.Length, impulseResponse.Length,
            peakIndex, Math.Abs(impulseResponse[peakIndex]), allFinite,
            ComplexMath.LinearToDb(bandMean), Ripple(response, 30, 140), Ripple(response, 20, 150), Tilt(response, 20, 150));
    }

    // ---- 1) length ----

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void Length_is_round_T_times_fs(double duration)
    {
        SweepSettings settings = Settings(duration);
        int expected = duration switch
        {
            0.5 => 24000,     // round(0.5 · 48000)
            1.0 => 48000,     // round(1.0 · 48000)
            2.0 => 96000,     // round(2.0 · 48000)
            4.0 => 192000,    // round(4.0 · 48000)
            _ => throw new ArgumentOutOfRangeException(nameof(duration)),
        };

        Assert.Equal(expected, settings.SampleCount);
        Assert.Equal((int)Math.Round(duration * Fs), settings.SampleCount);
        Assert.Equal(expected, SweepGenerator.GenerateExponentialSweep(settings).Length);
    }

    // ---- 2) the exponential law, per duration ----

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void Exponential_law_holds_at_every_duration(double duration)
    {
        // f(0) = f1·exp(0) = 20 Hz; f(T) = f1·exp(L) = 150 Hz; f(T/2) = √(f1·f2) = 54.772255750516614 Hz.
        // A LINEAR chirp would instead be f1 + (f2−f1)·t/T, whose midpoint (f1+f2)/2 = 85 Hz is +55 %.
        Assert.Equal(StartHz, FrequencyLaw(0.0, StartHz, EndHz, duration), 1e-6 * StartHz);
        Assert.Equal(EndHz, FrequencyLaw(duration, StartHz, EndHz, duration), 1e-6 * EndHz);
        Assert.Equal(GeometricMeanHz, FrequencyLaw(duration / 2.0, StartHz, EndHz, duration), 1e-6 * GeometricMeanHz);

        // The samples really carry the law: the analytic-signal phase slope at t = T/2 gives √(f1·f2) at every
        // duration. rel 1e-3 keeps SweepGeneratorTests' documented window-slope bias (it grows as the frequency
        // ramp steepens, i.e. at 0.5 s, where the measured bias is still below 1e-4 relative).
        SweepSettings settings = Settings(duration);
        double[] samples = SweepGenerator.GenerateExponentialSweep(settings, taper: false);
        double measured = MeasuredFrequency(samples, Fs, samples.Length / 2, halfWindow: 128);
        output.WriteLine($"T={duration}: measured midpoint frequency = {measured:R} Hz (law {GeometricMeanHz:R}, linear chirp 85)");
        Assert.Equal(GeometricMeanHz, measured, 1e-3 * GeometricMeanHz);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void Phase_integral_matches_the_cycle_count(double duration)
    {
        SweepSettings settings = Settings(duration);
        double[] samples = SweepGenerator.GenerateExponentialSweep(settings, taper: false);

        // cycles = ∫f dt = (f2 − f1)·T / ln(f2/f1) = 64.5187·T; a linear chirp would give (f1+f2)/2·T = 85·T,
        // so at every duration the two laws are tens of cycles apart and this is a real law assertion.
        double cycles = (EndHz - StartHz) * duration / Math.Log(EndHz / StartHz);
        int signChanges = 0;
        for (int i = 1; i < samples.Length; i++)
            if (samples[i] > 0.0 != samples[i - 1] > 0.0) signChanges++;

        output.WriteLine($"T={duration}: sign changes = {signChanges} (log sweep {2 * cycles:F1}, linear chirp {2 * 85.0 * duration:F1})");
        Assert.InRange(signChanges, 2.0 * cycles - 2.0, 2.0 * cycles + 2.0);
    }

    // ---- 3) deconvolution per duration ----

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void Reconstruction_peaks_at_zero_lag_and_carries_no_anomaly(double duration)
    {
        Reconstruction r = Reconstruct(duration);
        SweepSettings settings = Settings(duration);

        output.WriteLine($"T={duration}: N={r.SampleCount} IR len={r.ImpulseResponseLength} peak={r.PeakIndex} "
            + $"(zero lag {r.InverseLength - 1}) peakMagnitude={r.PeakMagnitude:R} bandMean={r.BandMeanDb:E3} dB "
            + $"interior ripple={r.InteriorRippleDb:F4} dB full={r.FullRippleDb:F4} dB tilt={r.TiltDb:F4} dB");

        Assert.Equal(settings.SampleCount, r.InverseLength);                    // same length as the excitation
        Assert.Equal(2 * settings.SampleCount - 1, r.ImpulseResponseLength);    // linear convolution length
        Assert.True(r.AllFinite, $"the {duration} s reconstruction carried a NaN or Inf sample");
        Assert.True(r.PeakMagnitude > 0.0 && double.IsFinite(r.PeakMagnitude), $"peak magnitude was {r.PeakMagnitude}");

        // zero-lag convention: an undelayed system peaks at inverseFilter.Length − 1 (InverseFilterTests at 1 s)
        Assert.InRange(r.PeakIndex - (r.InverseLength - 1), -1, 1);

        // the inverse filter is normalised so a unity-gain recording reports 0 dB in band — duration-independent
        Assert.Equal(0.0, r.BandMeanDb, 1e-9);

        // the ESS weighting direction is correct at every duration. Measured tilt: −1.0290 (0.5 s), −0.6257
        // (1 s), −0.4947 (2 s), −0.2910 dB (4 s) — all negative, magnitude falling with duration, and ≥ 5×
        // smaller than the nearest wrong construction (a linear-chirp inverse tilts −8.66 dB, a plain
        // correlation −18.25 dB, a reversed envelope −35.58 dB; InverseFilterTests). The 1.5 dB gate is pinned
        // to the measured 0.5 s worst case, not to the 1 s figure the shorter sweep does not meet.
        Assert.True(Math.Abs(r.TiltDb) <= 1.5, $"{duration} s in-band tilt was {r.TiltDb:F4} dB");
    }

    // ---- 4) cross-duration consistency: pinned table, duration-free ideal, no self-comparison ----

    [Fact]
    public void Reconstruction_is_consistent_across_the_four_durations()
    {
        double[] durations = [0.5, 1.0, 2.0, 4.0];
        Reconstruction[] reconstructions = [.. durations.Select(Reconstruct)];

        // (a) PEAK POSITION: relative to each duration's own zero-lag reference the peak is the same ±1 sample,
        // so the peak-shift pattern is duration-free.
        foreach (Reconstruction r in reconstructions)
            Assert.InRange(r.PeakIndex - (r.InverseLength - 1), -1, 1);

        // (b) PEAK AMPLITUDE: the ideal band-limited impulse has peak 2·(f2−f1)/fs = 0.00541667, independent of
        // the sweep duration. The measured reconstructions must stay near that duration-free value and near each
        // other — a duration-dependent anomaly (e.g. a normalisation that scales with N) shows up here.
        double idealPeak = 2.0 * (EndHz - StartHz) / Fs;
        double meanPeak = reconstructions.Average(r => r.PeakMagnitude);
        double spread = reconstructions.Max(r => r.PeakMagnitude) / reconstructions.Min(r => r.PeakMagnitude) - 1.0;
        output.WriteLine($"peak amplitude: ideal={idealPeak:R} mean={meanPeak:R} spread={spread:P3} | "
            + string.Join(", ", reconstructions.Select(r => $"{r.DurationSeconds}s={r.PeakMagnitude:R}")));
        // ponytail: 5 % is the measured 4-duration spread plus margin, not a derived bound; the ESS pulse is
        // dispersive, so it approaches but never equals the ideal band-limited 0.0054167 at finite T.
        Assert.True(spread <= 0.05, $"peak magnitude spread across durations was {spread:P3}");
        foreach (Reconstruction r in reconstructions)
            Assert.True(Math.Abs(r.PeakMagnitude - idealPeak) <= 0.05 * idealPeak,
                $"{r.DurationSeconds} s peak {r.PeakMagnitude:R} is more than 5 % from the ideal {idealPeak:R}");

        // (c) IN-BAND SHAPE: pinned measured table (EssEdgeMarginTests style). The interior 30–140 Hz ripple is
        // the duration-dependent part of the response; full-band 20–150 Hz is edge-dominated and ~12 dB at every
        // duration — the characterised ESS edge artifact, so it is pinned, not gated.
        var pins = new (double Duration, double Interior, double Full, double Tilt)[]
        {
            (0.5, 3.0999, 11.3729, -1.0290),
            (1.0, 2.7753, 12.1023, -0.6257),
            (2.0, 2.7987, 12.2021, -0.4947),
            (4.0, 2.2262, 12.0579, -0.2910),
        };
        output.WriteLine("interior 30-140 Hz ripple: "
            + string.Join(", ", reconstructions.Select(r => $"{r.DurationSeconds}s={r.InteriorRippleDb:F4} dB"))
            + " | full 20-150 Hz ripple: "
            + string.Join(", ", reconstructions.Select(r => $"{r.DurationSeconds}s={r.FullRippleDb:F4} dB"))
            + " | tilt: " + string.Join(", ", reconstructions.Select(r => $"{r.DurationSeconds}s={r.TiltDb:F4} dB")));
        foreach (Reconstruction r in reconstructions)
        {
            var pin = pins.Single(p => p.Duration == r.DurationSeconds);
            Assert.Equal(pin.Interior, r.InteriorRippleDb, 0.001);
            Assert.Equal(pin.Full, r.FullRippleDb, 0.001);
            Assert.Equal(pin.Tilt, r.TiltDb, 0.01);
        }

        // (d) the duration trend the data support: longer sweeps converge on the duration-free ideal — the peak
        // amplitude approaches 2·Δf/fs and the tilt magnitude shrinks, both monotonically; and the longest sweep
        // has the smallest interior ripple (the 4 s > 1 s ordering EssEdgeMarginTests measured on another band).
        Reconstruction shortest = reconstructions[0];   // 0.5 s: the worst measured case
        Reconstruction longest = reconstructions[^1];   // 4.0 s
        for (int i = 1; i < reconstructions.Length; i++)
        {
            Assert.True(Math.Abs(reconstructions[i].PeakMagnitude - idealPeak) < Math.Abs(reconstructions[i - 1].PeakMagnitude - idealPeak),
                $"peak {reconstructions[i].DurationSeconds}s = {reconstructions[i].PeakMagnitude:R} did not move closer to the ideal {idealPeak:R} than {reconstructions[i - 1].DurationSeconds}s");
            Assert.True(Math.Abs(reconstructions[i].TiltDb) < Math.Abs(reconstructions[i - 1].TiltDb),
                $"tilt magnitude did not shrink from {reconstructions[i - 1].DurationSeconds}s to {reconstructions[i].DurationSeconds}s");
        }
        Assert.True(longest.InteriorRippleDb <= shortest.InteriorRippleDb,
            $"4 s interior ripple {longest.InteriorRippleDb:F4} dB is worse than the 0.5 s {shortest.InteriorRippleDb:F4} dB");
        foreach (Reconstruction r in reconstructions)
            Assert.True(r.InteriorRippleDb <= 3.2, $"{r.DurationSeconds} s interior ripple {r.InteriorRippleDb:F4} dB outside the pinned envelope");
    }

    // ---- 5) the full shipped pipeline, at the unexercised 2.0 s and at 0.5 s ----

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void Full_pipeline_measures_a_clean_point_at_the_predicted_sample(double duration)
    {
        SimulationConfig config = SimulationConfig.Default with { ImageSourceOrder = 0, SweepSeconds = duration };
        MeasurementPoint microphone = ListeningRegion.Default.Points.Single(point => point.Id == "x0_y0_z0");
        var sub = new VirtualSubwoofer(new Position(0.30, 0.40, 0.35));

        // VirtualLab.Measure → PointMeasurement.Run is the shipped chain: sweep, inverse filter, deconvolution,
        // alignment, FFT, quality checks. The same geometry and prediction SimulationPipelineTests uses at 1 s.
        SimulatedMeasurement measurement = new VirtualLab(config, [sub], [microphone]).Measure(SubMode.A, microphone);
        PointMeasurementResult result = measurement.Result;

        double distance = sub.Position.DistanceTo(new Position(microphone.X, microphone.Y, microphone.Z));
        double expected = config.PreRollSeconds * config.SampleRate
            + distance / config.Room.SpeedOfSound * config.SampleRate;
        int measured = result.Alignment.PeakIndex - result.Alignment.ZeroLagIndex;

        output.WriteLine($"T={duration}: d={distance:R} m → expected {expected:R} samples from the recording start; "
            + $"measured {measured} (offset {measured - expected:+0.0;-0.0;0.0}), issues [{string.Join(", ", result.Issues)}]");

        Assert.Equal(config.Sweep.SampleCount, result.SweepSampleCount);
        // 2.0 s measured offset 0.0 samples; 0.5 s measured −1.026: the 0.5 s ESS reconstruction pulse itself
        // peaks one sample before the zero-lag reference (Reconstruction_peaks...: peak 23998 vs zero lag 23999)
        // plus the 0.026-sample fractional placement. The gate is one sample wider at 0.5 s rather than pretending
        // the pulse is symmetric at short durations; both are asserted against the pure propagation prediction,
        // which is duration-free.
        double tolerance = duration < 1.0 ? 2.0 : 1.0;
        Assert.InRange(measured - expected, -tolerance, 1.0);
        Assert.True(result.IsValid, $"the chain graded the {duration} s point invalid: {string.Join(", ", result.Issues)}");
    }
}
