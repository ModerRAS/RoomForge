namespace AudioOptimizer.Art.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;

public class PhaseCalibrationTests
{
    const double LowHz = 100;
    const double HighHz = 6000;

    [Fact]
    public void Known_minimum_phase_has_near_zero_excess()
    {
        double[] impulse = SyntheticSignals.MinPhase(0.5, 0.25, -0.4);
        PhaseDecomposition decomposition = MinimumPhaseDecomposition.Decompose(impulse, SyntheticSignals.SampleRate, 8192);

        double raw = MeanAbs(decomposition.ExcessPhaseRad, decomposition.FrequencyHz, LowHz, HighHz);
        double nonlinear = ExcessMetrics.NonlinearRms(decomposition, LowHz, HighHz).Rms;

        Assert.True(raw < 0.05, $"raw excess {raw}");
        Assert.True(nonlinear < 0.02, $"nonlinear excess {nonlinear}");
    }

    [Fact]
    public void Allpass_times_minimum_phase_recovers_excess()
    {
        const double pole = 0.85;
        double[] mixed = SyntheticSignals.Direct(
            SyntheticSignals.MinPhase(0.4, -0.3),
            SyntheticSignals.FirstOrderAllpass(pole, 512));
        PhaseDecomposition decomposition = MinimumPhaseDecomposition.Decompose(mixed, SyntheticSignals.SampleRate, 8192);
        double[] analytic = SyntheticSignals.AllpassPhase(pole, decomposition.FrequencyHz, SyntheticSignals.SampleRate);
        double error = SyntheticSignals.AlignedRms(decomposition.ExcessPhaseRad, analytic, decomposition.FrequencyHz, LowHz, HighHz);
        double nonlinear = ExcessMetrics.NonlinearRms(decomposition, LowHz, HighHz).Rms;

        Assert.True(nonlinear > 0.15, $"allpass nonlinear excess was only {nonlinear}");
        Assert.True(error < 0.12, $"excess vs analytic allpass RMS {error}");
    }

    [Fact]
    public void Common_excess_matches_the_shared_allpass_and_refuses_one_point()
    {
        const double sharedPole = 0.5;
        const double extraPole = 0.8;
        double[] shared = SyntheticSignals.FirstOrderAllpass(sharedPole, 512);
        double[] extra = SyntheticSignals.FirstOrderAllpass(extraPole, 512);
        PhaseDecomposition[] decompositions =
        [
            MinimumPhaseDecomposition.Decompose(SyntheticSignals.Direct(SyntheticSignals.MinPhase(0.2, 0.5), shared), SyntheticSignals.SampleRate, 8192),
            MinimumPhaseDecomposition.Decompose(SyntheticSignals.Direct(SyntheticSignals.MinPhase(0.3, -0.25), shared), SyntheticSignals.SampleRate, 8192),
            MinimumPhaseDecomposition.Decompose(SyntheticSignals.Direct(SyntheticSignals.Direct(SyntheticSignals.MinPhase(0.15, 0.45), shared), extra), SyntheticSignals.SampleRate, 8192),
        ];

        CommonExcess common = CommonExcessEstimator.Estimate(decompositions);
        double[] analytic = SyntheticSignals.AllpassPhase(sharedPole, common.FrequencyHz, SyntheticSignals.SampleRate);
        double sharedError = SyntheticSignals.AlignedRms(common.ExcessPhaseRad, analytic, common.FrequencyHz, LowHz, HighHz);
        double contaminatedError = SyntheticSignals.AlignedRms(
            common.ExcessPhaseRad, decompositions[2].ExcessPhaseRad, common.FrequencyHz, LowHz, HighHz);

        Assert.True(sharedError < 0.45, $"common vs shared allpass {sharedError}");
        Assert.True(contaminatedError > sharedError, $"common collapsed onto the contaminated position ({contaminatedError} vs {sharedError})");
        Assert.Throws<ArtValidationException>(() => CommonExcessEstimator.Estimate([decompositions[0]]));
    }

    [Fact]
    public void Calibration_reduces_excess_and_respects_prering()
    {
        const int fft = 8192;
        var options = new PhaseCalibrationOptions
        {
            FirLength = fft,
            ModelingDelayMs = 20,
            MaxPreRingMs = 2,
            MaxPreRingDb = -40,
            MetricLowHz = LowHz,
            MetricHighHz = HighHz,
        };
        ImpulseResponse[] positions = Positions(sharedPole: 0.5, contaminateLast: false);

        PhaseCalibrationResult result = new PhaseCalibrator().Calibrate("L", positions, options);

        Assert.Equal(PhaseCalibrationOptions.Decomposition, result.Decomposition);
        Assert.True(result.PhaseFir.Length > 0);
        Assert.Equal(result.Preview.ImpulseBefore.Length, result.Preview.ImpulseAfter.Length);
        Assert.Equal(result.Preview.FrequencyHz.Length, result.Preview.ExcessResidualRad.Length);
        Assert.True(result.Preview.ExcessMetricAfter < result.Preview.ExcessMetricBefore * 0.5,
            $"before {result.Preview.ExcessMetricBefore} after {result.Preview.ExcessMetricAfter}");

        int peak = SyntheticSignals.ArgMaxAbs(result.PhaseFir);
        int allowed = (int)Math.Round(options.MaxPreRingMs * SyntheticSignals.SampleRate / 1000.0);
        double peakAbs = Math.Abs(result.PhaseFir[peak]);
        double limit = peakAbs * Math.Pow(10.0, options.MaxPreRingDb / 20.0);
        for (int i = 0; i < peak - allowed; i++)
            Assert.True(Math.Abs(result.PhaseFir[i]) <= limit * 1.01 + 1e-12, $"pre-ring sample {i}");
        Assert.InRange(peak, result.ModelingDelaySamples - 80, result.ModelingDelaySamples + 80);
    }

    [Fact]
    public void Pure_magnitude_eq_does_not_clear_the_excess_metric()
    {
        const double pole = 0.55;
        double[] mixed = SyntheticSignals.Direct(
            SyntheticSignals.MinPhase(0.4, -0.3),
            SyntheticSignals.FirstOrderAllpass(pole, 512));
        double[] equalizer = MinimumPhaseEqualizer.InvertMagnitude(mixed, 4096);
        double[] equalized = Fft.Convolve(mixed, equalizer);
        int n = Fft.NextPowerOfTwo(Math.Max(equalized.Length, 8192));
        double before = ExcessMetrics.Measure(mixed, SyntheticSignals.SampleRate, LowHz, HighHz, n);
        double afterEq = ExcessMetrics.Measure(equalized, SyntheticSignals.SampleRate, LowHz, HighHz, n);

        var options = new PhaseCalibrationOptions
        {
            FirLength = 8192,
            ModelingDelayMs = 20,
            MaxPreRingMs = 2,
            MaxPreRingDb = -40,
            MetricLowHz = LowHz,
            MetricHighHz = HighHz,
        };
        PhaseCalibrationResult calibrated = new PhaseCalibrator().Calibrate("L", Positions(pole, contaminateLast: false), options);

        Assert.True(afterEq > before * 0.75, $"magnitude EQ excess {afterEq} vs before {before}");
        Assert.True(calibrated.Preview.ExcessMetricAfter < calibrated.Preview.ExcessMetricBefore * 0.5,
            $"phase cal {calibrated.Preview.ExcessMetricAfter} vs {calibrated.Preview.ExcessMetricBefore}");
        Assert.True(calibrated.Preview.ExcessMetricAfter < afterEq * 0.5,
            $"phase {calibrated.Preview.ExcessMetricAfter} was not below magnitude EQ {afterEq}");
    }

    [Fact]
    public void Single_position_is_rejected_and_supports_are_off_by_default()
    {
        ImpulseResponse[] positions = Positions(0.4, contaminateLast: false);
        Assert.Throws<ArtValidationException>(() => new PhaseCalibrator().Calibrate("L", [positions[0]], new PhaseCalibrationOptions()));

        var session = new ArtSessionDocument
        {
            Channels =
            [
                new ArtChannelConfig { ChannelId = "L", Role = SpeakerRole.Primary },
                new ArtChannelConfig { ChannelId = "R", Role = SpeakerRole.Support },
            ],
        };
        var responses = new Dictionary<string, IReadOnlyList<ImpulseResponse>>
        {
            ["L"] = positions,
            ["R"] = positions,
        };

        IReadOnlyList<PhaseCalibrationResult> primaryOnly = ArtPhasePlanner.CalibrateSession(session, responses, new PhaseCalibrationOptions
        {
            FirLength = 4096,
            ModelingDelayMs = 10,
            MaxPreRingMs = 2,
        });
        Assert.Single(primaryOnly);
        Assert.Equal("L", primaryOnly[0].ChannelId);
        Assert.NotEmpty(primaryOnly[0].PhaseFir);

        IReadOnlyList<PhaseCalibrationResult> withSupports = ArtPhasePlanner.CalibrateSession(session, responses, new PhaseCalibrationOptions
        {
            FirLength = 4096,
            ModelingDelayMs = 10,
            MaxPreRingMs = 2,
            ApplyToSupports = true,
        });
        Assert.Equal(2, withSupports.Count);
        Assert.Contains(withSupports, result => result.ChannelId == "R");
    }

    static ImpulseResponse[] Positions(double sharedPole, bool contaminateLast)
    {
        double[] shared = SyntheticSignals.FirstOrderAllpass(sharedPole, 512);
        double[] third = contaminateLast
            ? SyntheticSignals.Direct(shared, SyntheticSignals.FirstOrderAllpass(0.8, 512))
            : shared;
        return
        [
            SyntheticSignals.Ir(SyntheticSignals.Direct(SyntheticSignals.MinPhase(0.2, 0.5), shared)),
            SyntheticSignals.Ir(SyntheticSignals.Direct(SyntheticSignals.MinPhase(0.3, -0.25), shared)),
            SyntheticSignals.Ir(SyntheticSignals.Direct(SyntheticSignals.MinPhase(0.15, 0.45), third)),
        ];
    }

    static double MeanAbs(double[] values, double[] frequencyHz, double lowHz, double highHz)
    {
        double sum = 0;
        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (frequencyHz[i] < lowHz || frequencyHz[i] > highHz) continue;
            sum += Math.Abs(values[i]);
            count++;
        }
        return count == 0 ? double.PositiveInfinity : sum / count;
    }
}
