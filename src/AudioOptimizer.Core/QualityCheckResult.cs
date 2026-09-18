namespace AudioOptimizer.Core;

/// <summary>
/// What one quality check found: the named reasons it failed for, and nothing else. No log text, no boolean
/// (an empty list is the pass condition, so a check can report several reasons at once).
/// </summary>
public sealed record QualityCheckResult(IReadOnlyList<QualityIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public static QualityCheckResult Pass { get; } = new([]);

    public static QualityCheckResult Fail(params QualityIssue[] issues) => new(issues);
}
