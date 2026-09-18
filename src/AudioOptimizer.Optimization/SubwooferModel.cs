namespace AudioOptimizer.Optimization;

using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;

/// <summary>
/// The forward model: what does setting S do to the dual-sub response at every position?
/// <para>
/// H_B'(f,i) = G · H_B(f,i) · exp(jφ) · polarity · exp(−j2πfΔt), then H_total(f,i) = H_A(f,i) + H_B'(f,i).
/// The sum is a complex sum — magnitudes are added as vectors, never in dB. Two equal subs 180° apart
/// cancel (0), they do not add to +6 dB.
/// </para>
/// </summary>
public static class SubwooferModel
{
    /// <summary>Predicts the dual-sub response at every position for one setting of B (A fixed at 0 dB).</summary>
    public static IReadOnlyList<PositionResponse> Combine(DualSubMeasurement measurement, SubwooferSetting setting)
    {
        measurement.Validate();
        setting.Validate();

        double gain = setting.GainLinear;
        var positions = new PositionResponse[measurement.A.Count];

        for (int i = 0; i < positions.Length; i++)
        {
            PositionResponse a = measurement.A[i];
            PositionResponse b = measurement.B[i];
            var bins = new FrequencyResponse[a.Bins.Count];

            for (int k = 0; k < bins.Length; k++)
            {
                var ha = new Complex(a.Bins[k].Real, a.Bins[k].Imag);
                var hb = new Complex(b.Bins[k].Real, b.Bins[k].Imag);

                // H_B' = G · H_B · exp(jφ) · polarity · exp(−j2πfΔt)
                Complex hbPrime = ComplexMath.Rotate(hb, setting.PhaseRad);          // exp(jφ)
                hbPrime = ComplexMath.ApplyDelay(hbPrime, a.Bins[k].FrequencyHz, setting.DelaySeconds); // exp(−j2πfΔt)
                hbPrime *= setting.Polarity * gain;                                  // ±, 10^(G/20)

                bins[k] = Bin(a.Bins[k].FrequencyHz, ha + hbPrime);
            }

            positions[i] = new PositionResponse(a.PointId, a.AnalysisBand, bins);
        }

        return positions;
    }

    /// <summary>
    /// Re-states one complex value in the <see cref="FrequencyResponse"/> shape. PhaseUnwrappedRad equals
    /// PhaseWrappedRad here: unwrapping is a property of a bin <em>sequence</em>, and this is one bin —
    /// call <see cref="ComplexMath.Unwrap"/> on PhaseWrappedRad if the continuous branch is needed.
    /// </summary>
    public static FrequencyResponse Bin(double frequencyHz, Complex value)
        => new(frequencyHz, value.Real, value.Imaginary,
            ComplexMath.LinearToDb(ComplexMath.Magnitude(value)),
            ComplexMath.Phase(value),
            ComplexMath.Phase(value));
}
