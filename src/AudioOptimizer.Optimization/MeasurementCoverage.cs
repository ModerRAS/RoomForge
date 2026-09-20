namespace AudioOptimizer.Optimization;

/// <summary>The scope a spatial-uniformity guarantee actually covers. Ordered from weakest to strongest.</summary>
public enum SpatialGuarantee
{
    /// <summary>Only the positions actually measured. Every other position in the session or region is unknown.</summary>
    MeasuredPositionsOnly,

    /// <summary>Every position the session planned to measure, but not necessarily every position of the declared region.</summary>
    WholeMeasuredSession,

    /// <summary>Every position of the declared listening region.</summary>
    WholeDeclaredRegion,
}

/// <summary>
/// What area a recommendation's spatial claim is valid over, as data on the result. The optimizer is handed a set of
/// measured positions and nothing else: it can only speak about those. Whether that set is a complete session and
/// whether the session is the whole declared region is product knowledge, supplied by the caller, and the difference
/// is exactly the difference between "the room is even" and "the room is even where we looked".
/// <para>
/// There is deliberately no extrapolation path. A position that was not measured is unknown; the result says so
/// instead of guessing, and the required user-facing wording for every partial case is
/// <see cref="MeasuredRegionOnlyWarning"/>.
/// </para>
/// </summary>
public sealed record MeasurementCoverage(
    int MeasuredPositionCount,
    int? SessionPositionCount,
    int DeclaredRegionPositionCount,
    SpatialGuarantee Guarantee,
    string WarningText)
{
    /// <summary>The required user-facing sentence for any result that does not cover the whole declared region. Verbatim.</summary>
    public const string MeasuredRegionOnlyWarning = "本次结果只对已测位置提供空间均匀性保证。";

    /// <summary>What is said when the measured set really is the whole declared region.</summary>
    public const string WholeDeclaredRegionStatement = "This result covers every position of the declared listening region.";

    /// <summary>True only when every position of the declared region was measured.</summary>
    public bool CoversWholeDeclaredRegion => Guarantee == SpatialGuarantee.WholeDeclaredRegion;

    /// <summary>Positions the session planned but did not deliver; null when the session size is unknown.</summary>
    public int? MissingSessionPositions => SessionPositionCount is { } session ? Math.Max(0, session - MeasuredPositionCount) : null;

    /// <summary>Positions of the declared region outside the measured set; 0 when the region size is unknown.</summary>
    public int MissingDeclaredRegionPositions => Math.Max(0, DeclaredRegionPositionCount - MeasuredPositionCount);

    /// <summary>
    /// Classifies a measured set against the session that was planned and the region that was declared. Counts, not
    /// coordinates: the caller knows which positions were intended, the optimizer only knows how many it got.
    /// A session count <b>equal</b> to the measured count means the planned session completed; a session count larger
    /// means it did not. A declared region is only satisfied when every declared position was measured.
    /// </summary>
    public static MeasurementCoverage Classify(int measuredPositionCount, int? sessionPositionCount = null, int declaredRegionPositionCount = 0)
    {
        if (measuredPositionCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(measuredPositionCount), measuredPositionCount, "A coverage statement needs at least one measured position.");
        if (sessionPositionCount is <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionPositionCount), sessionPositionCount, "A session size is either null (unknown) or a positive count.");
        if (declaredRegionPositionCount < 0)
            throw new ArgumentOutOfRangeException(nameof(declaredRegionPositionCount), declaredRegionPositionCount, "The declared region size cannot be negative.");

        if (declaredRegionPositionCount > 0 && measuredPositionCount >= declaredRegionPositionCount)
            return new MeasurementCoverage(measuredPositionCount, sessionPositionCount, declaredRegionPositionCount,
                SpatialGuarantee.WholeDeclaredRegion, WholeDeclaredRegionStatement);

        if (sessionPositionCount is { } session && measuredPositionCount == session)
            return new MeasurementCoverage(measuredPositionCount, sessionPositionCount, declaredRegionPositionCount,
                SpatialGuarantee.WholeMeasuredSession, MeasuredRegionOnlyWarning);

        return new MeasurementCoverage(measuredPositionCount, sessionPositionCount, declaredRegionPositionCount,
            SpatialGuarantee.MeasuredPositionsOnly, MeasuredRegionOnlyWarning);
    }
}
