namespace AudioOptimizer.Optimization;

using AudioOptimizer.Core;

/// <summary>
/// One measurement position: the complex response bins measured at a single grid point, inside the band the sweep
/// excited. The band is required and validated on construction, so a full-spectrum bin list cannot be built at all —
/// a filter that zeroed out-of-band bins instead of removing them would leave sums, row counts and maxima unchanged,
/// which is why the guard is the type rather than a check inside each consumer.
/// </summary>
/// <remarks>
/// Exception contract, stated once here rather than repeated in four test files, so the type stays the source of truth:
/// argument shape (null or whitespace) → ArgumentNullException / ArgumentException, a constructor precondition;
/// intra-object invariant (a bin outside its own band) → ArgumentOutOfRangeException, the constructor;
/// inter-object relation (grids or bands disagree) → ArgumentException, DualSubMeasurement.Validate.
/// The three are disjoint, and xUnit's Assert.Throws is exact, so the derived type in an assertion alone says which
/// guard fired (ArgumentNullException and ArgumentOutOfRangeException both derive from ArgumentException).
/// The type discriminates only under an exact match; do not catch the base and think you know the category.
/// </remarks>
public sealed record PositionResponse
{
    public PositionResponse(string pointId, FrequencyBand analysisBand, IReadOnlyList<FrequencyResponse> bins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pointId);
        ArgumentNullException.ThrowIfNull(bins);
        foreach (FrequencyResponse bin in bins)
            if (!analysisBand.Contains(bin.FrequencyHz))
                throw new ArgumentOutOfRangeException(nameof(bins), bin.FrequencyHz,
                    $"Bin {bin.FrequencyHz} Hz is outside the analysis band {analysisBand}: a position carries only the frequencies its sweep excited.");

        PointId = pointId;
        AnalysisBand = analysisBand;
        Bins = bins;
    }

    public string PointId { get; }

    /// <summary>
    /// AnalysisBand constrains the bins on this response; it never selects them. The bins were selected once, upstream,
    /// when the session built the response (<see cref="AudioOptimizer.Measurement.MeasurementSession.InBand"/>). Changing
    /// this field cannot change which bins are present — only whether this response validates.
    /// Required, with no default meaning "no band".
    /// </summary>
    public FrequencyBand AnalysisBand { get; }

    /// <summary>The in-band bins, exactly as measured. Not settable, so there is no path that skips the constructor.</summary>
    public IReadOnlyList<FrequencyResponse> Bins { get; }
}

/// <summary>
/// "These two positions are comparable": the same band, and the same frequency at every index. One implementation,
/// used by <see cref="DualSubMeasurement.Validate"/> (the engine's entry contract), by
/// <see cref="SpatialMetrics.Compute"/> and by <see cref="AbValidation"/> — a second copy of this loop is exactly how
/// the rule drifts, and one had already grown back.
/// Convention: grid comparison goes through this class. Every comparison of two `FrequencyHz` values
/// in the solution is `FirstDifferentBin` — search the `.cs` files for `FrequencyHz` immediately
/// followed by `!=`; it returns exactly one hit. That is a spot check, not a proof: a comparison
/// written as a set or dictionary keyed by frequency, or with an epsilon tolerance, would evade it
/// while being a genuine second implementation of the rule.
/// </summary>
public static class PositionGrid
{
    /// <summary>Plain pairwise equality of two frequency vectors: same order, same frequency at every index. Equality —
    /// reflexive, symmetric and transitive — so the set-form caller may delegate to one reference position.</summary>
    public static bool SameGrid(PositionResponse left, PositionResponse right) => FirstDifferentBin(left, right) < 0;

    /// <summary>The index of the first bin whose frequency differs, or −1 when the grids agree.</summary>
    public static int FirstDifferentBin(PositionResponse left, PositionResponse right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        int count = Math.Min(left.Bins.Count, right.Bins.Count);
        for (int k = 0; k < count; k++)
            if (left.Bins[k].FrequencyHz != right.Bins[k].FrequencyHz) return k;

        return -1;
    }
}

/// <summary>
/// The optimizer's input contract: exactly what the measurement engine produced, one grid pass per
/// configuration. A and B are the two subwoofers, AB is the measured dual-sub pass — carried so a real
/// session can be handed straight to the optimizer, but never read back as a prediction: the model must
/// predict totals from A and B, or it would just be reporting the measurement back to itself.
/// N is whatever the data has. Nothing here knows about 27.
/// </summary>
    /// AB is deliberately settable and validates on USE (see Validate): it is optional, and a required member cannot be absent.
public sealed record DualSubMeasurement(
    IReadOnlyList<PositionResponse> A,
    IReadOnlyList<PositionResponse> B,
    IReadOnlyList<PositionResponse>? AB = null)
{
    public int PositionCount => A.Count;

    /// <summary>
    /// A and B must cover the same positions on the same frequency grid, or a complex sum would pair
    /// unrelated bins and return a number nobody can trust. Throws instead of returning one.
    /// </summary>
    /// The use-path check is not redundant now that construction validates: a constructor can only enforce properties of one
    /// object, and both remaining invariants — the same band, and the same frequency at every index — are relations
    /// between two objects, which no single constructor can see.
    public void Validate()
    {
        if (A.Count == 0) throw new ArgumentException("A measurement needs at least one position.", nameof(A));
        if (A.Count != B.Count)
            throw new ArgumentException($"A covers {A.Count} positions, B covers {B.Count}; they must match.", nameof(B));

        for (int i = 0; i < A.Count; i++)
        {
            PositionResponse a = A[i];
            PositionResponse b = B[i];
            if (a.Bins.Count != b.Bins.Count)
                throw new ArgumentException($"Position {a.PointId} has {a.Bins.Count} bins in A and {b.Bins.Count} in B.", nameof(B));
            if (a.Bins.Count == 0)
                throw new ArgumentException($"Position {a.PointId} has no bins.", nameof(A));

            // was, so a mismatch would be laundered into a combined position that passes validation. FrequencyBand is a
            // readonly record struct, so == is value equality.
            if (a.AnalysisBand != b.AnalysisBand)
                throw new ArgumentException(
                    $"Position {a.PointId} covers {a.AnalysisBand} in A and {b.PointId} covers {b.AnalysisBand} in B.",
                    nameof(B));

            int different = PositionGrid.FirstDifferentBin(a, b);
            if (different >= 0)
                throw new ArgumentException(
                    $"Position {a.PointId} bin {different} is {a.Bins[different].FrequencyHz} Hz in A and {b.Bins[different].FrequencyHz} Hz in B.",
                    nameof(B));
        }

        // The measured A+B pass is cloned from A by the same `with`, so it needs the same two checks or a mismatch on
        // that path would be laundered too.
        if (AB is { Count: > 0 } ab)
        {
            if (ab.Count != A.Count)
                throw new ArgumentException($"A covers {A.Count} positions, the A+B pass covers {ab.Count}; they must match.", nameof(AB));

            for (int i = 0; i < A.Count; i++)
            {
                PositionResponse a = A[i];
                PositionResponse measured = ab[i];
                if (a.AnalysisBand != measured.AnalysisBand)
                    throw new ArgumentException(
                        $"Position {a.PointId} covers {a.AnalysisBand} in A and {measured.AnalysisBand} in the A+B pass.",
                        nameof(AB));

                int different = PositionGrid.FirstDifferentBin(a, measured);
                if (different >= 0)
                    throw new ArgumentException(
                        $"Position {a.PointId} bin {different} is {a.Bins[different].FrequencyHz} Hz in A and {measured.Bins[different].FrequencyHz} Hz in the A+B pass.",
                        nameof(AB));
            }
        }
    }
}
