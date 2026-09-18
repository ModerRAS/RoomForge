namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;

/// <summary>
/// The verdict type. A point is valid only by having no reasons at all, so "invalid with no reason" — the shape
/// that makes a validation failure impossible to diagnose later — cannot be constructed.
/// </summary>
public sealed class MeasurementOutcomeTests
{
    [Fact]
    public void Valid_means_no_reasons_at_all()
    {
        Assert.True(MeasurementOutcome.Clean.IsValid);
        Assert.Empty(MeasurementOutcome.Clean.Reasons);
        Assert.True(new QualityCheckResult([]).IsValid);          // an empty issue list is the pass condition
        Assert.Empty(QualityCheckResult.Pass.Issues);
    }

    [Fact]
    public void An_invalid_outcome_must_carry_at_least_one_named_reason()
    {
        MeasurementOutcome outcome = MeasurementOutcome.Invalid(QualityIssue.OutputClipping, QualityIssue.DropoutDetected);

        Assert.False(outcome.IsValid);
        Assert.Equal([QualityIssue.OutputClipping, QualityIssue.DropoutDetected], outcome.Reasons);
        Assert.Throws<ArgumentException>(() => MeasurementOutcome.Invalid());
    }

    [Fact]
    public void From_collects_distinct_reasons_from_every_check_in_check_order()
    {
        // Two checks fail; the same reason reported twice must appear once (two checks can share a cause class).
        MeasurementOutcome outcome = MeasurementOutcome.From(
            QualityCheckResult.Pass,
            QualityCheckResult.Fail(QualityIssue.LowSignalToNoise),
            QualityCheckResult.Fail(QualityIssue.DropoutDetected, QualityIssue.LowSignalToNoise));

        Assert.False(outcome.IsValid);
        Assert.Equal([QualityIssue.LowSignalToNoise, QualityIssue.DropoutDetected], outcome.Reasons);

        // All checks passing is the valid outcome.
        Assert.True(MeasurementOutcome.From(QualityCheckResult.Pass, QualityCheckResult.Pass).IsValid);
    }
}
