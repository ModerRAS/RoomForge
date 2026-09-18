namespace AudioOptimizer.Tests.Visualization;

using System.IO;
using System.Reflection;
using AudioOptimizer.Visualization;
using Xunit.Abstractions;

/// <summary>
/// The level reference decides both the label and the colour mapping, and the "SPL" state must be
/// unconstructable without the calibration it claims. Two different tests: one exercises every branch
/// (including the one production cannot reach today), the other proves production cannot reach it.
/// </summary>
public sealed class LevelReferenceTests(ITestOutputHelper output)
{
    [Fact]
    public void Each_reference_produces_its_own_label_and_colour_scale()
    {
        var calibration = new MicrophoneCalibration("umik-1-7001234.txt", 94.0);
        LevelReference[] references =
        [
            LevelReference.RelativeToBandMean,
            LevelReference.NormalisedToMax,
            LevelReference.SplCalibrated(calibration),
        ];

        Assert.Equal(
            [LevelReferenceKind.RelativeToBandMean, LevelReferenceKind.NormalisedToMax, LevelReferenceKind.SplCalibrated],
            references.Select(reference => reference.Kind));

        foreach (LevelReference reference in references)
        {
            ColourScale scale = ColourScale.For(reference);
            Assert.Equal(reference.AxisLabel, scale.Reference.AxisLabel);
            Assert.Contains(reference.AxisLabel, scale.Label);
            Assert.True(scale.MaxDb > scale.MinDb);
            output.WriteLine($"{reference.Kind,-20} label '{reference.AxisLabel}' scale [{scale.MinDb}, {scale.MaxDb}] end colours "
                + $"{scale.ColourAt(scale.MinDb).ToHex()} → {scale.ColourAt(scale.MaxDb).ToHex()}");
        }

        // The SPL label is derived from the calibration payload, so it cannot claim SPL anonymously.
        LevelReference spl = references[2];
        Assert.Contains("umik-1-7001234.txt", spl.AxisLabel);
        Assert.Contains("94.0", spl.AxisLabel);
        Assert.Contains("SPL", spl.AxisLabel);
    }

    [Fact]
    public void Colour_endpoints_and_mapping_come_from_the_reference_not_from_a_constant()
    {
        // Endpoints: centred on its mean (-12…12), bounded by 0 dB (-30…0), absolute window (60…100 dB SPL).
        Assert.Equal((-12.0, 12.0), LevelReference.RelativeToBandMean.DefaultRangeDb);
        Assert.Equal((-30.0, 0.0), LevelReference.NormalisedToMax.DefaultRangeDb);
        Assert.Equal((60.0, 100.0), LevelReference.SplCalibrated(new MicrophoneCalibration("cal.txt", 90.0)).DefaultRangeDb);

        ColourScale relative = ColourScale.For(LevelReference.RelativeToBandMean);
        ColourScale normalised = ColourScale.For(LevelReference.NormalisedToMax);

        // The same level therefore maps to different colours: the reference moves the scale, not just the text.
        Assert.NotEqual(relative.ColourAt(0).ToHex(), normalised.ColourAt(0).ToHex());
        Assert.Equal("#D6202A", relative.ColourAt(12).ToHex());     // ramp top, red
        Assert.Equal("#0B3D91", normalised.ColourAt(-30).ToHex());  // ramp bottom, deep blue
        Assert.Equal(0.0, normalised.FractionAt(-30), 1e-12);
        Assert.Equal(1.0, normalised.FractionAt(0), 1e-12);
        Assert.Equal(0.5, normalised.FractionAt(-15), 1e-12);        // midpoint of [-30, 0]
        Assert.Equal(1.0, normalised.FractionAt(5), 1e-12);          // clamped above the top
    }

    [Fact]
    public void Levels_are_restated_in_the_reference_own_units()
    {
        var context = new LevelContext(BandMeanDb: -26.0, MaxDb: -20.0);

        // Relative to band mean: −20 − (−26) = +6 dB above the mean.
        Assert.Equal(6.0, LevelReference.RelativeToBandMean.ToReferenceUnits(-20.0, context), 1e-12);

        // Normalised to max: −26 − (−20) = −6 dB below the loudest bin.
        Assert.Equal(-6.0, LevelReference.NormalisedToMax.ToReferenceUnits(-26.0, context), 1e-12);

        // Calibrated: −20 + 94 = 74 dB SPL. The offset IS the calibration; it is not stored anywhere else.
        Assert.Equal(74.0, LevelReference.SplCalibrated(new MicrophoneCalibration("cal.txt", 94.0)).ToReferenceUnits(-20.0, context), 1e-12);
    }

    [Fact]
    public void Spl_cannot_be_expressed_without_a_calibration()
    {
        // A required argument, not an optional flag: null is refused...
        Assert.Throws<ArgumentNullException>(() => LevelReference.SplCalibrated(null!));

        // ...and a calibration with no identity or a non-finite sensitivity is not a calibration either, so the
        // "unrepresentable state" holds through the payload as well as the signature.
        Assert.Throws<ArgumentException>(() => new MicrophoneCalibration("  ", 94.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MicrophoneCalibration("cal.txt", double.NaN));

        // There is no parameterless or bool-taking way to ask for SPL: the factory's only argument is the
        // calibration payload.
        MethodInfo factory = typeof(LevelReference).GetMethod(nameof(LevelReference.SplCalibrated))!;
        Assert.Equal([typeof(MicrophoneCalibration)], factory.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void No_production_code_path_can_produce_a_calibrated_reference_today()
    {
        // 1) API surface: every public static VALUE of this type is a relative variant.
        LevelReference[] reachableValues =
        [
            .. typeof(LevelReference)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(property => property.PropertyType == typeof(LevelReference))
                .Select(property => (LevelReference)property.GetValue(null)!),
        ];
        Assert.NotEmpty(reachableValues);
        Assert.All(reachableValues, reference => Assert.NotEqual(LevelReferenceKind.SplCalibrated, reference.Kind));
        // The claim that matters is that no reachable label mentions SPL: without a calibration the words
        // "dB SPL" cannot appear anywhere in the product.
        Assert.All(reachableValues, reference => Assert.DoesNotContain("SPL", reference.AxisLabel, StringComparison.Ordinal));

        // 2) Closed hierarchy: the base has a private constructor and no public one, so no other assembly can
        //    add a variant (including a calibrated one) of its own.
        Assert.Empty(typeof(LevelReference).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Contains(typeof(LevelReference).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance), c => c.IsPrivate);

        // 3) Source scan: no production call site mentions either the calibrated variant or the calibration
        //    payload type. This is the assertion that fails the day someone wires SPL without a calibration
        //    source, which is stronger than a comment saying they should not.
        string[] productionFiles = [.. Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)];
        Assert.NotEmpty(productionFiles);
        string[] splCallSites = [.. productionFiles.Where(file => File.ReadAllText(file).Contains("SplCalibrated", StringComparison.Ordinal))];
        string[] calibrationTypes = [.. productionFiles.Where(file => File.ReadAllText(file).Contains("MicrophoneCalibration", StringComparison.Ordinal))];

        string definition = Path.Combine("src", "AudioOptimizer.Visualization", "LevelReference.cs");
        Assert.Equal([Path.Combine(TestPaths.RepoRoot, definition)], splCallSites);
        Assert.Equal([Path.Combine(TestPaths.RepoRoot, definition)], calibrationTypes);
        output.WriteLine($"SplCalibrated appears in {splCallSites.Length} production file(s); no calibration source exists in src/.");
    }
}
