namespace AudioOptimizer.Art;

using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;

/// <summary>
/// Estimates excess phase that is shared across listening positions and designs a mixed-phase FIR
/// that drives that component toward a pure delay, inside a modeling-delay and pre-ring budget.
/// A single position is refused: the default path is not an unconstrained one-point inverse.
/// </summary>
public sealed class PhaseCalibrator
{
    public const double MinimumCoherence = 0.25;

    public PhaseCalibrationResult Calibrate(
        string channelId,
        IReadOnlyList<ImpulseResponse> positions,
        PhaseCalibrationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(positions);
        options ??= new PhaseCalibrationOptions();
        Validate(positions, options);

        int sampleRate = positions[0].SampleRate;
        int longest = 0;
        foreach (ImpulseResponse position in positions) longest = Math.Max(longest, position.Samples.Length);
        int n = ArtFourier.PowerOfTwoAtLeast(Math.Max(options.FirLength, longest));

        var decompositions = new PhaseDecomposition[positions.Count];
        for (int i = 0; i < positions.Count; i++)
            decompositions[i] = MinimumPhaseDecomposition.Decompose(positions[i].Samples, sampleRate, n);

        CommonExcess common = CommonExcessEstimator.Estimate(decompositions);
        int delay = (int)Math.Round(options.ModelingDelayMs * sampleRate / 1000.0);
        delay = Math.Clamp(delay, 0, n - 1);

        Complex[] correction = BuildCorrection(common, delay, n);
        double[] fir = ArtFourier.InverseReal(correction);
        ApplyPreRingGate(fir, sampleRate, options);

        double[] before = new double[n];
        positions[0].Samples.CopyTo(before, 0);
        Complex[] observed = ArtFourier.ForwardReal(before, n);
        // The pre-ring gate edits the FIR after BuildCorrection. Replay that FIR so the preview
        // matches the coefficients that will be exported.
        Complex[] gated = ArtFourier.ForwardReal(fir, n);
        var corrected = new Complex[n];
        for (int k = 0; k < n; k++) corrected[k] = observed[k] * gated[k];
        double[] after = ArtFourier.InverseReal(corrected);

        PhaseDecomposition beforeDecomp = decompositions[0];
        PhaseDecomposition afterDecomp = MinimumPhaseDecomposition.Decompose(after, sampleRate, n);
        ExcessBand beforeBand = ExcessMetrics.NonlinearRms(beforeDecomp, options.MetricLowHz, options.MetricHighHz);
        ExcessBand afterBand = ExcessMetrics.NonlinearRms(afterDecomp, options.MetricLowHz, options.MetricHighHz);

        return new PhaseCalibrationResult
        {
            ChannelId = channelId,
            PhaseFir = fir,
            PhaseCalVersion = "1",
            Decomposition = PhaseCalibrationOptions.Decomposition,
            ModelingDelaySamples = delay,
            SampleRate = sampleRate,
            Preview = new PhasePreview
            {
                ImpulseBefore = before,
                ImpulseAfter = after,
                FrequencyHz = afterBand.FrequencyHz,
                ExcessResidualRad = afterBand.ResidualRad,
                ExcessMetricBefore = beforeBand.Rms,
                ExcessMetricAfter = afterBand.Rms,
            },
        };
    }

    static void Validate(IReadOnlyList<ImpulseResponse> positions, PhaseCalibrationOptions options)
    {
        var errors = new List<string>();
        if (positions.Count < 2)
            errors.Add("Phase calibration needs at least two positions; a single-point unconstrained inverse is not the default.");
        if (options.FirLength < 32) errors.Add("Phase FIR length must be at least 32.");
        if (options.MaxPreRingMs < 0) errors.Add("Max pre-ring duration cannot be negative.");
        if (!double.IsFinite(options.MaxPreRingDb)) errors.Add("Max pre-ring level must be finite.");
        if (options.ModelingDelayMs < options.MaxPreRingMs)
            errors.Add("Modeling delay must be at least the allowed pre-ring duration.");
        if (options.MetricLowHz < 0 || options.MetricHighHz <= options.MetricLowHz)
            errors.Add("Metric band is empty.");

        int sampleRate = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            ImpulseResponse position = positions[i];
            if (position is null || position.Samples is null || position.Samples.Length == 0)
            {
                errors.Add($"Position {i} has no impulse response.");
                continue;
            }

            if (sampleRate == 0) sampleRate = position.SampleRate;
            else if (position.SampleRate != sampleRate) errors.Add("Positions do not share a sample rate.");
            if (position.SampleRate <= 0) errors.Add($"Position {i} has a non-positive sample rate.");
        }

        if (errors.Count > 0) throw new ArtValidationException(errors);
    }

    static Complex[] BuildCorrection(CommonExcess common, int delaySamples, int n)
    {
        var correction = new Complex[n];
        int bins = n / 2 + 1;
        for (int k = 0; k < bins; k++)
        {
            double excess = common.Coherence[k] < MinimumCoherence ? 0 : common.ExcessPhaseRad[k];
            double phase = -excess - 2.0 * Math.PI * k * delaySamples / n;
            correction[k] = Complex.FromPolarCoordinates(1.0, phase);
        }

        correction[0] = new Complex(correction[0].Real, 0);
        if (n % 2 == 0) correction[n / 2] = new Complex(Math.Cos(correction[n / 2].Phase), 0);
        for (int k = 1; k < n / 2; k++) correction[n - k] = Complex.Conjugate(correction[k]);
        return correction;
    }

    static void ApplyPreRingGate(double[] fir, int sampleRate, PhaseCalibrationOptions options)
    {
        int peak = 0;
        double peakAbs = 0;
        for (int i = 0; i < fir.Length; i++)
        {
            double abs = Math.Abs(fir[i]);
            if (abs > peakAbs)
            {
                peakAbs = abs;
                peak = i;
            }
        }

        if (peakAbs < 1e-12) throw new ArtValidationException("Phase FIR is empty.");

        int allowed = (int)Math.Round(options.MaxPreRingMs * sampleRate / 1000.0);
        int gateUntil = peak - Math.Max(allowed, 0);
        if (gateUntil <= 0) return;

        double prefixPeak = 0;
        for (int i = 0; i < gateUntil; i++) prefixPeak = Math.Max(prefixPeak, Math.Abs(fir[i]));
        double limit = peakAbs * Math.Pow(10.0, options.MaxPreRingDb / 20.0);
        if (prefixPeak <= limit || prefixPeak <= 0) return;

        double scale = limit / prefixPeak;
        for (int i = 0; i < gateUntil; i++) fir[i] *= scale;
    }
}

/// <summary>Complex mean of per-position excess phasors. Requires two or more decompositions.</summary>
public static class CommonExcessEstimator
{
    public static CommonExcess Estimate(IReadOnlyList<PhaseDecomposition> decompositions)
    {
        ArgumentNullException.ThrowIfNull(decompositions);
        if (decompositions.Count < 2)
            throw new ArtValidationException("Common excess needs at least two positions.");

        PhaseDecomposition first = decompositions[0];
        int bins = first.FrequencyHz.Length;
        for (int i = 1; i < decompositions.Count; i++)
        {
            if (decompositions[i].FftSize != first.FftSize || decompositions[i].SampleRate != first.SampleRate)
                throw new ArtValidationException("Decompositions do not share an FFT size and sample rate.");
        }

        var sum = new Complex[bins];
        foreach (PhaseDecomposition decomposition in decompositions)
        {
            for (int k = 0; k < bins; k++)
                sum[k] += Complex.FromPolarCoordinates(1.0, decomposition.ExcessPhaseRad[k]);
        }

        var wrapped = new double[bins];
        var coherence = new double[bins];
        double count = decompositions.Count;
        for (int k = 0; k < bins; k++)
        {
            coherence[k] = sum[k].Magnitude / count;
            wrapped[k] = coherence[k] < PhaseCalibrator.MinimumCoherence ? 0 : sum[k].Phase;
        }

        return new CommonExcess
        {
            FftSize = first.FftSize,
            SampleRate = first.SampleRate,
            FrequencyHz = first.FrequencyHz,
            ExcessPhaseRad = ComplexMath.Unwrap(wrapped),
            Coherence = coherence,
        };
    }
}

/// <summary>
/// Runs phase calibration on the session's primary, and on supports only when
/// <see cref="PhaseCalibrationOptions.ApplyToSupports"/> is set.
/// </summary>
public static class ArtPhasePlanner
{
    public static IReadOnlyList<PhaseCalibrationResult> CalibrateSession(
        ArtSessionDocument session,
        IReadOnlyDictionary<string, IReadOnlyList<ImpulseResponse>> responsesByChannel,
        PhaseCalibrationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(responsesByChannel);
        options ??= new PhaseCalibrationOptions();

        List<ArtChannelConfig> primaries = session.Channels.Where(channel => channel.Role == SpeakerRole.Primary).ToList();
        if (primaries.Count != 1)
            throw new ArtValidationException("Phase planning needs exactly one primary channel.");

        var calibrator = new PhaseCalibrator();
        var results = new List<PhaseCalibrationResult>
        {
            calibrator.Calibrate(primaries[0].ChannelId, Require(responsesByChannel, primaries[0].ChannelId), options),
        };

        if (!options.ApplyToSupports) return results;

        foreach (ArtChannelConfig support in session.Channels.Where(channel => channel.Role == SpeakerRole.Support))
            results.Add(calibrator.Calibrate(support.ChannelId, Require(responsesByChannel, support.ChannelId), options));
        return results;
    }

    static IReadOnlyList<ImpulseResponse> Require(
        IReadOnlyDictionary<string, IReadOnlyList<ImpulseResponse>> responsesByChannel,
        string channelId)
    {
        if (!responsesByChannel.TryGetValue(channelId, out IReadOnlyList<ImpulseResponse>? responses) || responses.Count == 0)
            throw new ArtValidationException($"Channel '{channelId}' has no impulse responses.");
        return responses;
    }
}

/// <summary>Primary export must carry a phase FIR unless phase bypass is explicitly set.</summary>
public static class PrimaryPathPolicy
{
    public static void EnsurePhaseFir(bool phaseBypass, double[]? phaseFir)
    {
        if (!phaseBypass && (phaseFir is null || phaseFir.Length == 0))
            throw new ArtValidationException("Primary path requires a non-empty phase FIR unless phase bypass is set.");
    }
}
