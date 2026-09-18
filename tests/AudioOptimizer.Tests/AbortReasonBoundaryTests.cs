namespace AudioOptimizer.Tests;

using System.Reflection;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;

/// <summary>
/// <see cref="QualityIssue.AbortedInFlight"/> is the one member of that enum which is a <b>user action</b> rather
/// than a signal-quality verdict. That keeps the representation cheap (one enum member instead of a union type or
/// a parallel field), and the price is a boundary that must be enforced: the eight quality checks are the only
/// other producer of that enum, and they must never be able to say "the user aborted".
/// <para>
/// The proof is mechanical rather than behavioural: a source scan of the Dsp project (where every check lives)
/// for the member's name, plus a positive control proving the same scan reads real source and finds other members
/// of the same enum, plus a check that the consumer side <i>does</i> name it, so this test cannot pass by the
/// feature being unwired. Same pattern as the thread-freedom guard.
/// </para>
/// </summary>
public sealed class AbortReasonBoundaryTests
{
    /// <summary>The eight checks that produce quality verdicts. Named so "the checks" is a set, not a claim.</summary>
    private static readonly string[] CheckNames =
    [
        "CheckInputClipping",
        "CheckOutputClipping",
        "CheckSweepCompleteness",
        "CheckImpulseResponseFound",
        "CheckSignalToNoise",
        "CheckImpulseResponseOutlier",
        "CheckRecordingLength",
        "CheckDropouts",
    ];

    [Fact]
    public void The_eight_quality_checks_all_live_in_the_dsp_layer()
    {
        Type checks = typeof(QualityChecks);
        Assert.Equal("AudioOptimizer.Dsp", checks.Assembly.GetName().Name);

        foreach (string name in CheckNames)
            Assert.NotNull(checks.GetMethod(name, BindingFlags.Public | BindingFlags.Static));

        // The eight names are the whole verdict-producing surface: an unlisted public method would be a ninth
        // producer nobody guarded. (Return-type check keeps a helper from counting as a check.)
        string[] producers = [.. checks.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(QualityCheckResult))
            .Select(method => method.Name)
            .Order()];
        Assert.Equal([.. CheckNames.Order()], producers);
    }

    [Fact]
    public void No_quality_check_source_ever_names_the_abort_reason()
    {
        string dsp = Path.Combine(TestPaths.SourceRoot, "AudioOptimizer.Dsp");
        Assert.True(Directory.Exists(dsp), $"{dsp} does not exist: the scan would have nothing to read.");

        string[] files = [.. Directory.GetFiles(dsp, "*.cs", SearchOption.AllDirectories)];
        Assert.True(files.Length >= 5, $"Only {files.Length} source files found under {dsp}: the scan is not reading the layer.");

        // Positive control: the same scan does find other reasons of the same enum, so a zero-hit result for
        // AbortedInFlight means absence, not a broken reader.
        Assert.Contains(files, file => File.ReadAllText(file).Contains("DropoutDetected", StringComparison.Ordinal));

        string[] offenders = [.. files.Where(file => File.ReadAllText(file).Contains("AbortedInFlight", StringComparison.Ordinal))];
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_abort_reason_is_defined_and_actually_produced_outside_the_checks()
    {
        Assert.True(Enum.IsDefined(QualityIssue.AbortedInFlight), "the abort reason must exist to be recorded.");

        // If nothing produced it, the scan above would pass while the feature was missing.
        string ui = Path.Combine(TestPaths.SourceRoot, "AudioOptimizer.UI");
        string[] producers = [.. Directory.GetFiles(ui, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("AbortedInFlight", StringComparison.Ordinal))];
        Assert.NotEmpty(producers);
    }

    // ponytail: a source scan catches a literal name, not a computed one (Enum.Parse over a string). No code in
    // Dsp parses this enum from text, so the gap is empty today; the upgrade is an IL member-reference scan.
}
