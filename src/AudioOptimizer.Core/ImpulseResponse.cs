namespace AudioOptimizer.Core;

/// <summary>
/// A deconvolved impulse response, possibly a cropped window of a longer deconvolution.
/// The absolute position of the peak is <see cref="AbsolutePeakIndex"/> = OffsetSamples + PeakIndex:
/// cropping keeps the offset instead of re-zeroing it, because a delay that silently reset to zero
/// would make every later phase/alignment decision wrong.
/// </summary>
public sealed record ImpulseResponse(double[] Samples, int SampleRate, int PeakSearchStart = 0)
{
    /// <summary>Index of the impulse peak inside <see cref="Samples"/> (set by FindPeak/WithPeak).</summary>
    public int PeakIndex { get; init; }

    /// <summary>Where Samples[0] came from in the original (uncropped) deconvolution.</summary>
    public int OffsetSamples { get; init; }

    /// <summary>Peak position in the original deconvolution timeline — invariant under Crop.</summary>
    public int AbsolutePeakIndex => OffsetSamples + PeakIndex;

    /// <summary>
    /// argmax|x[n]| over the half-open range [from, to). Defaults to [PeakSearchStart, Samples.Length),
    /// so a caller can exclude the pre-arrival noise floor (e.g. start the search where the direct
    /// sound is expected and let the raw leading samples go unused).
    /// </summary>
    public int FindPeak(int? from = null, int? to = null)
    {
        if (Samples.Length == 0) throw new InvalidOperationException("Cannot find a peak in an empty impulse response.");

        int start = Math.Clamp(from ?? PeakSearchStart, 0, Samples.Length - 1);
        int end = Math.Clamp(to ?? Samples.Length, start + 1, Samples.Length);

        int peak = start;
        double best = Math.Abs(Samples[start]);
        for (int i = start + 1; i < end; i++)
        {
            double candidate = Math.Abs(Samples[i]);
            if (candidate > best)
            {
                best = candidate;
                peak = i;
            }
        }
        return peak;
    }

    /// <summary>Returns a copy with <see cref="PeakIndex"/> set from <see cref="FindPeak"/>.</summary>
    public ImpulseResponse WithPeak(int? from = null, int? to = null)
        => this with { PeakIndex = FindPeak(from, to) };

    /// <summary>
    /// Peak-aligned window covering [PeakIndex - pre, PeakIndex + post] (inclusive of both sides),
    /// clamped to the available samples. The returned PeakIndex is relative to the new Samples, and
    /// OffsetSamples absorbs the shift, so AbsolutePeakIndex is preserved exactly.
    /// </summary>
    public ImpulseResponse Crop(int pre, int post)
    {
        if (Samples.Length == 0) throw new InvalidOperationException("Cannot crop an empty impulse response.");
        if (pre < 0 || post < 0) throw new ArgumentOutOfRangeException(nameof(pre), "Crop needs non-negative pre/post margins.");

        int start = Math.Max(0, PeakIndex - pre);
        int end = Math.Min(Samples.Length, PeakIndex + post + 1);
        if (end <= start) throw new ArgumentException("Crop window contains no samples.", nameof(pre));

        return this with
        {
            Samples = Samples[start..end],
            PeakIndex = PeakIndex - start,
            OffsetSamples = OffsetSamples + start,
        };
    }
}
