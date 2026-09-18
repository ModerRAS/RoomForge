namespace AudioOptimizer.Core;

/// <summary>
/// The verdict for one measured point: valid, or invalid with the named reasons collected from every quality
/// check. A point with no reasons is valid by construction, so there is no way to report "invalid, no reason".
/// </summary>
public sealed record MeasurementOutcome(bool IsValid, IReadOnlyList<QualityIssue> Reasons)
{
    public static MeasurementOutcome Clean { get; } = new(true, []);

    public static MeasurementOutcome Invalid(params QualityIssue[] reasons)
        => reasons.Length == 0
            ? throw new ArgumentException("An invalid outcome needs at least one named reason.", nameof(reasons))
            : new(false, reasons);

    /// <summary>Collects the reasons from every check of a point; the reasons stay distinct and in check order.</summary>
    public static MeasurementOutcome From(params QualityCheckResult[] checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var reasons = new List<QualityIssue>();
        foreach (QualityCheckResult check in checks)
            foreach (QualityIssue issue in check.Issues)
                if (!reasons.Contains(issue)) reasons.Add(issue);

        return reasons.Count == 0 ? Clean : new(false, reasons);
    }
}
