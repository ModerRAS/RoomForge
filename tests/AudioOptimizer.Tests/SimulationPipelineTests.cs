namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.Measurement;
using AudioOptimizer.Simulation;
using Xunit.Abstractions;

/// <summary>
/// The integration: the simulator's virtual recording through the product's own measurement chain, and the result
/// compared against the ground truth the simulator kept. Nothing here re-implements the chain — every response in this
/// file was produced by <c>PointMeasurement.Run</c>, the same call the hardware runner and the smoke test make.
/// </summary>
public class SimulationPipelineTests(ITestOutputHelper output)
{
    private static readonly MeasurementPoint Centre = ListeningRegion.Default.Points.Single(point => point.Id == "x0_y0_z0");

    private static readonly Position SubPosition = new(0.30, 0.40, 0.35);

    /// <summary>One sub, one microphone, direct sound only: the smallest configuration the chain can measure.</summary>
    private static SimulationConfig DirectOnly(SimulationConfig? config = null)
        => (config ?? SimulationConfig.Default) with { ImageSourceOrder = 0 };

    private static SimulatedMeasurement MeasureA(SimulationConfig config, VirtualSubwoofer sub, MeasurementPoint? point = null)
    {
        MeasurementPoint microphone = point ?? Centre;
        return new VirtualLab(config, [sub], [microphone]).Measure(SubMode.A, microphone);
    }

    /// <summary>
    /// The chain's own signature: the sweep deconvolved by its inverse filter with no room in the way, scaled by the
    /// rig's own gains (the playback gain scales the drive signal before it is played, and the microphone gain scales
    /// what is recorded — measured: forgetting the 0.25 gain puts a flat 12.04 dB offset into every comparison).
    /// Subtracting it is what isolates the room, exactly as <c>OfflineMeasurementTests</c> does with a unity-gain
    /// reference — the ESS reconstruction pulse carries a ±2.4 dB ripple across the band that belongs to the sweep,
    /// not to the room.
    /// </summary>
    private static FrequencyResponse[] ChainReference(SimulationConfig config, int fftSize)
    {
        double[] excitation = SweepGenerator.GenerateExponentialSweep(config.Sweep);
        double[] inverse = InverseFilter.BuildExponentialInverseSweep(config.Sweep);
        double[] unityGain = Deconvolver.Deconvolve(excitation, inverse);
        double scale = config.PlaybackGain * config.MicrophoneGain;
        for (int i = 0; i < unityGain.Length; i++) unityGain[i] *= scale;

        return FrequencyResponseCalculator.Compute(
            new ImpulseResponse(unityGain, (int)config.Sweep.SampleRate), WindowType.Rectangular, fftSize);
    }

    private static FrequencyResponse Nearest(IReadOnlyList<FrequencyResponse> bins, double frequencyHz)
        => bins.OrderBy(bin => Math.Abs(bin.FrequencyHz - frequencyHz)).First();

    [Fact]
    public void The_measured_impulse_response_lands_on_the_predicted_sample()
    {
        SimulationConfig config = DirectOnly();
        SimulatedMeasurement measurement = MeasureA(config, new VirtualSubwoofer(SubPosition));

        double distance = SubPosition.DistanceTo(new Position(Centre.X, Centre.Y, Centre.Z));
        double expectedSamples = config.PreRollSeconds * config.SampleRate + (distance / config.Room.SpeedOfSound * config.SampleRate);
        int measuredSamples = measurement.Result.Alignment.PeakIndex - measurement.Result.Alignment.ZeroLagIndex;

        output.WriteLine($"d = {distance:R} m → {expectedSamples:R} samples from the recording start; the chain measured "
            + $"{measuredSamples} (offset {measuredSamples - expectedSamples:+0.0;-0.0;0.0}), zero-lag reference "
            + $"{measurement.Result.Alignment.ZeroLagIndex}, issues [{string.Join(", ", measurement.Result.Issues)}]");

        Assert.True(measurement.Result.IsValid, $"the chain graded this point invalid: {string.Join(", ", measurement.Result.Issues)}");
        Assert.InRange(measuredSamples - expectedSamples, -1.0, 1.0);
    }

    [Fact]
    public void The_measured_response_matches_the_ground_truth_it_was_built_from()
    {
        SimulationConfig config = SimulationConfig.Default;
        var sub = new VirtualSubwoofer(new Position(0.45, 0.45, 0.35), GainDb: -1.5, DelaySeconds: 0.001);
        SimulatedMeasurement measurement = MeasureA(config, sub);

        // The ground truth is on the chain's own frequency grid; checking that first is what makes the comparison below
        // a physics check rather than a resolution artefact.
        Assert.Equal(config.CaptureFftSize, measurement.Result.FftSize);

        FrequencyResponse[] reference = ChainReference(config, measurement.Result.FftSize);
        GroundTruthPosition truth = new VirtualLab(config, [sub], [Centre]).GroundTruth.At(Centre.Id);

        double maxMagnitudeErrorDb = 0.0, maxPhaseErrorDegrees = 0.0;
        int bins = 0;
        foreach (FrequencyResponse bin in measurement.Result.Response)
        {
            // 25–145 Hz, not the full band: the sweep's own Fresnel roll-off at the very edges of 20–150 Hz is part of
            // the excitation, and this comparison is about the room.
            if (bin.FrequencyHz is < 25.0 or > 145.0) continue;
            FrequencyResponse chain = Nearest(reference, bin.FrequencyHz);
            FrequencyResponse expected = Nearest(truth.BinsA, bin.FrequencyHz);
            Assert.Equal(bin.FrequencyHz, expected.FrequencyHz, 6);        // same grid, so the nearest bin IS this bin

            double magnitudeError = (bin.MagnitudeDb - chain.MagnitudeDb) - expected.MagnitudeDb;
            maxMagnitudeErrorDb = Math.Max(maxMagnitudeErrorDb, Math.Abs(magnitudeError));

            // The measurement's phase is stated from the start of the RECORDING; the ground truth's from the start of the
            // room's response. The pre-roll between them is exactly known, so it is removed rather than tolerated.
            double preRollPhase = Math.Tau * bin.FrequencyHz * config.PreRollSeconds;
            maxPhaseErrorDegrees = Math.Max(maxPhaseErrorDegrees, Math.Abs(Math.IEEERemainder(
                (bin.PhaseWrappedRad - chain.PhaseWrappedRad) - expected.PhaseWrappedRad + preRollPhase, Math.Tau)) * 180.0 / Math.PI);
            bins++;
        }

        output.WriteLine($"{bins} bins over 25–145 Hz on the chain's own grid: max |Δ| = {maxMagnitudeErrorDb:F4} dB, "
            + $"max phase error = {maxPhaseErrorDegrees:F4}° (response − chain ripple, against the ground truth)");

        Assert.True(bins > 400, $"only {bins} bins were compared");
        Assert.True(maxMagnitudeErrorDb < 0.05, $"the chain missed the ground-truth level by {maxMagnitudeErrorDb:F4} dB");
        Assert.True(maxPhaseErrorDegrees < 0.5, $"the chain missed the ground-truth phase by {maxPhaseErrorDegrees:F4}°");
    }

    [Fact]
    public void A_phase_setting_rotates_the_measured_response_by_exactly_that_angle()
    {
        SimulationConfig config = SimulationConfig.Default;
        SimulatedMeasurement unrotated = MeasureA(config, new VirtualSubwoofer(SubPosition));
        SimulatedMeasurement rotated = MeasureA(config, new VirtualSubwoofer(SubPosition, PhaseDegrees: 90.0));

        FrequencyResponse[] reference = ChainReference(config, unrotated.Result.FftSize);
        double maxMagnitudeErrorDb = 0.0, maxPhaseErrorDegrees = 0.0;
        int bins = 0;
        foreach (FrequencyResponse bin in rotated.Result.Response)
        {
            if (bin.FrequencyHz is < 25.0 or > 145.0) continue;
            FrequencyResponse plain = Nearest(unrotated.Result.Response, bin.FrequencyHz);
            FrequencyResponse chainPlain = Nearest(reference, bin.FrequencyHz);
            FrequencyResponse chainRotated = Nearest(reference, bin.FrequencyHz);

            // Compare the two measurements' room responses, with the chain's own ripple divided out of both.
            double plainDb = plain.MagnitudeDb - chainPlain.MagnitudeDb;
            double rotatedDb = bin.MagnitudeDb - chainRotated.MagnitudeDb;
            maxMagnitudeErrorDb = Math.Max(maxMagnitudeErrorDb, Math.Abs(rotatedDb - plainDb));

            double plainPhase = plain.PhaseWrappedRad - chainPlain.PhaseWrappedRad;
            double rotatedPhase = bin.PhaseWrappedRad - chainRotated.PhaseWrappedRad;
            maxPhaseErrorDegrees = Math.Max(maxPhaseErrorDegrees, Math.Abs(Math.IEEERemainder(
                rotatedPhase - plainPhase - (Math.PI / 2.0), Math.Tau)) * 180.0 / Math.PI);
            bins++;
        }

        output.WriteLine($"{bins} bins: a 90° phase setting moved the measured level by at most {maxMagnitudeErrorDb:F4} dB "
            + $"and the measured phase by {maxPhaseErrorDegrees:F4}° away from 90°");

        // The ideal rotation is magnitude-preserving and phase-exact; what is left is the chain's own residual, which
        // is why these are 0.000x and not 0.00x.
        Assert.True(maxMagnitudeErrorDb < 0.05, $"the rotation changed the level by {maxMagnitudeErrorDb:F4} dB");
        Assert.True(maxPhaseErrorDegrees < 0.5, $"the rotation was {maxPhaseErrorDegrees:F4}° off");
    }

    [Fact]
    public void A_delay_setting_shifts_the_measured_impulse_response_by_that_time()
    {
        SimulationConfig config = DirectOnly();
        var sub = new VirtualSubwoofer(SubPosition);
        int plain = MeasureA(config, sub).Result.Alignment.PeakIndex;
        int delayed = MeasureA(config, sub with { DelaySeconds = 0.0025 }).Result.Alignment.PeakIndex;

        int expected = (int)Math.Round(0.0025 * config.SampleRate);
        output.WriteLine($"a 2.5 ms delay moved the peak from {plain} to {delayed} samples: {delayed - plain} against {expected}");

        Assert.InRange(delayed - plain - expected, -1, 1);
    }

    [Fact]
    public void The_a_b_capture_is_the_sum_of_the_two_recordings()
    {
        // No noise: the identity is about the signal path, and the capture's own noise floor is a separate, deliberate
        // addition to each recording.
        SimulationConfig config = DirectOnly() with { MicrophoneNoiseLevel = 0.0 };
        var a = new VirtualSubwoofer(new Position(0.30, 0.30, 0.35));
        var b = new VirtualSubwoofer(new Position(3.00, 3.30, 0.35), GainDb: -3.0, PhaseDegrees: 90.0);
        VirtualLab lab = new(config, [a, b], [Centre]);

        SimulatedMeasurement measurementA = lab.Measure(SubMode.A, Centre);
        SimulatedMeasurement measurementB = lab.Measure(SubMode.B, Centre);
        SimulatedMeasurement measurementAb = lab.Measure(SubMode.AB, Centre);

        double maxRecordingDifference = 0.0;
        for (int i = 0; i < measurementAb.Result.Recording.Length; i++)
            maxRecordingDifference = Math.Max(maxRecordingDifference,
                Math.Abs(measurementAb.Result.Recording[i] - (measurementA.Result.Recording[i] + measurementB.Result.Recording[i])));

        output.WriteLine($"max |recording(A+B) − recording(A) − recording(B)| = {maxRecordingDifference:E3} "
            + $"over {measurementAb.Result.Recording.Length} samples; peak levels {measurementA.Result.PeakMagnitude:E3}, "
            + $"{measurementB.Result.PeakMagnitude:E3}, {measurementAb.Result.PeakMagnitude:E3}");

        Assert.True(maxRecordingDifference < 1e-12, $"the A+B capture differs from the sum of the two by {maxRecordingDifference:E3}");

        // And the same statement in the frequency domain, in band: the A+B response is the complex sum of A and B —
        // never a magnitude or dB sum, which would put a different number in every bin.
        double maxComplexError = 0.0;
        double maxScale = 0.0;
        foreach (FrequencyResponse bin in measurementAb.Result.Response)
        {
            if (bin.FrequencyHz is < 25.0 or > 145.0) continue;
            FrequencyResponse left = Nearest(measurementA.Result.Response, bin.FrequencyHz);
            FrequencyResponse right = Nearest(measurementB.Result.Response, bin.FrequencyHz);
            double expectedRe = left.Real + right.Real, expectedIm = left.Imag + right.Imag;
            maxComplexError = Math.Max(maxComplexError, Math.Sqrt(Math.Pow(bin.Real - expectedRe, 2) + Math.Pow(bin.Imag - expectedIm, 2)));
            maxScale = Math.Max(maxScale, Math.Sqrt((bin.Real * bin.Real) + (bin.Imag * bin.Imag)));
        }

        output.WriteLine($"complex-sum error {maxComplexError:E3} against a response scale of {maxScale:E3}");
        Assert.True(maxComplexError < 1e-9 * Math.Max(1.0, maxScale), $"the A+B response is not the complex sum: {maxComplexError:E3}");
    }

    [Fact]
    public void The_summed_impulse_response_is_the_sum_of_the_impulse_responses_and_of_their_spectra()
    {
        SimulationConfig config = SimulationConfig.Default;
        var a = new VirtualSubwoofer(new Position(0.30, 0.30, 0.35));
        var b = new VirtualSubwoofer(new Position(3.00, 3.30, 0.35), GainDb: -3.0, PhaseDegrees: 90.0);
        VirtualRoom rig = new VirtualLab(config, [a, b], [Centre]).Rig;

        double[] combined = rig.CombinedImpulseResponse(0);
        double[] left = rig.ImpulseResponseOf(0, 0);
        double[] right = rig.ImpulseResponseOf(1, 0);

        double maxIrDifference = 0.0;
        for (int i = 0; i < combined.Length; i++)
            maxIrDifference = Math.Max(maxIrDifference, Math.Abs(combined[i] - (left[i] + right[i])));
        Assert.Equal(0.0, maxIrDifference, 12);

        // ESS ⊛ (IR_A + IR_B) = ESS ⊛ IR_A + ESS ⊛ IR_B, and the same for the transforms the chain uses.
        double[] sweep = SweepGenerator.GenerateExponentialSweep(config.Sweep);
        double[] combinedConvolution = Fft.Convolve(sweep, combined);
        double[] leftConvolution = Fft.Convolve(sweep, left);
        double[] rightConvolution = Fft.Convolve(sweep, right);
        double maxConvolutionDifference = 0.0;
        for (int i = 0; i < combinedConvolution.Length; i++)
            maxConvolutionDifference = Math.Max(maxConvolutionDifference,
                Math.Abs(combinedConvolution[i] - (leftConvolution[i] + rightConvolution[i])));

        FrequencyResponse[] combinedBins = FrequencyResponseCalculator.Compute(new ImpulseResponse(combined, config.SampleRate), WindowType.Rectangular);
        FrequencyResponse[] leftBins = FrequencyResponseCalculator.Compute(new ImpulseResponse(left, config.SampleRate), WindowType.Rectangular);
        FrequencyResponse[] rightBins = FrequencyResponseCalculator.Compute(new ImpulseResponse(right, config.SampleRate), WindowType.Rectangular);
        double maxFftError = 0.0, scale = 0.0;
        for (int k = 0; k < combinedBins.Length; k++)
        {
            if (combinedBins[k].FrequencyHz is < 25.0 or > 145.0) continue;
            double expectedRe = leftBins[k].Real + rightBins[k].Real, expectedIm = leftBins[k].Imag + rightBins[k].Imag;
            maxFftError = Math.Max(maxFftError, Math.Sqrt(Math.Pow(combinedBins[k].Real - expectedRe, 2) + Math.Pow(combinedBins[k].Imag - expectedIm, 2)));
            scale = Math.Max(scale, combinedBins[k].MagnitudeDb is double.NegativeInfinity ? 0 : Math.Pow(10.0, combinedBins[k].MagnitudeDb / 20.0));
        }

        output.WriteLine($"IR sum error {maxIrDifference:E3}; ESS⊗(A+B) vs ESS⊗A + ESS⊗B error {maxConvolutionDifference:E3}; "
            + $"FFT(A+B) vs FFT(A)+FFT(B) error {maxFftError:E3} against a scale of {scale:E3}");

        Assert.True(maxConvolutionDifference < 1e-9, $"the convolutions disagree by {maxConvolutionDifference:E3}");
        Assert.True(maxFftError < 1e-9 * scale, $"the transforms disagree by {maxFftError:E3}");
    }

    [Fact]
    public void The_noise_floor_is_reproducible_and_exactly_zero_when_it_is_switched_off()
    {
        SimulationConfig noisy = DirectOnly();
        SimulationConfig silent = DirectOnly() with { MicrophoneNoiseLevel = 0.0 };
        var sub = new VirtualSubwoofer(SubPosition);

        double[] first = MeasureA(noisy, sub).Result.Recording;
        double[] second = MeasureA(noisy, sub).Result.Recording;
        double[] silentFirst = MeasureA(silent, sub).Result.Recording;

        Assert.Equal(first, second);                                     // byte-for-byte, not "close"
        Assert.True(MeasureA(noisy with { NoiseSeed = noisy.NoiseSeed + 1 }, sub).Result.Recording
            .Zip(first).Any(pair => pair.First != pair.Second), "a different seed must produce a different capture");

        int preRoll = (int)Math.Round(silent.PreRollSeconds * silent.SampleRate);
        Assert.True(silentFirst.Take(preRoll).All(sample => sample == 0.0), "a zero noise floor must render exact silence");

        // The noise is a floor: its RMS is what was asked for, and it leaves the measured peak alone to within a
        // hundredth of a dB.
        double measuredRms = Math.Sqrt(first.Take(preRoll).Select(sample => sample * sample).Average());
        output.WriteLine($"noise floor RMS measured over the pre-roll: {measuredRms:E4} (configured {noisy.MicrophoneNoiseLevel:E4})");
        Assert.InRange(measuredRms, noisy.MicrophoneNoiseLevel * 0.9, noisy.MicrophoneNoiseLevel * 1.1);
    }

    [Fact]
    public void The_whole_chain_is_bit_identical_when_it_is_run_twice()
    {
        SimulationConfig config = SimulationConfig.Default;
        var sub = new VirtualSubwoofer(new Position(0.45, 0.45, 0.35), GainDb: -2.0, PhaseDegrees: 30.0, DelaySeconds: 0.0015);

        PointMeasurementResult first = MeasureA(config, sub).Result;
        PointMeasurementResult second = MeasureA(config, sub).Result;

        Assert.NotEmpty(first.ImpulseResponse);
        Assert.Equal(first.Recording, second.Recording);
        Assert.Equal(first.ImpulseResponse, second.ImpulseResponse);
        Assert.Equal(first.PeakMagnitude, second.PeakMagnitude);
        Assert.Equal(first.Alignment.PeakIndex, second.Alignment.PeakIndex);
        Assert.Equal(
            first.Response.Select(bin => (bin.Real, bin.Imag, bin.MagnitudeDb)),
            second.Response.Select(bin => (bin.Real, bin.Imag, bin.MagnitudeDb)));
    }
}
