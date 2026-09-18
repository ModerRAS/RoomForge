namespace AudioOptimizer.Core;

/// <summary>
/// The band a measurement excited, as a value rather than two loose doubles passed between signatures: start and end
/// frequency are the same type, so a transposed pair compiles and would band-limit to nothing. Inclusive at both edges,
/// matching the sweep's own 20–150 Hz description and the bin count derived from it.
/// </summary>
/// <remarks>
/// There is deliberately no default and no <c>None</c>: a value meaning "no band" is the full-spectrum defect expressed
/// in an API's shape, so the only way to obtain a band is from the sweep that produced the measurement.
/// </remarks>
public readonly record struct FrequencyBand(double StartHz, double EndHz)
{
    /// <summary>The band of the sweep that produced a measurement. The one seam where a band is created.</summary>
    public static FrequencyBand Of(SweepSettings sweep)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        return new FrequencyBand(sweep.StartHz, sweep.EndHz);
    }

    /// <summary>Inclusive at both edges: a bin exactly at the sweep's start or end frequency was excited by it.</summary>
    public bool Contains(double frequencyHz) => frequencyHz >= StartHz && frequencyHz <= EndHz;

    public double SpanHz => EndHz - StartHz;

    public override string ToString() => $"{StartHz:0.##}–{EndHz:0.##} Hz";
}
