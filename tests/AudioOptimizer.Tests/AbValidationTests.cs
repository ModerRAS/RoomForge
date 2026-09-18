namespace AudioOptimizer.Tests;

using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using Xunit.Abstractions;

/// <summary>
/// §21: does the linear-superposition model describe the rig? The prediction must come from
/// <see cref="SubwooferModel"/> (the same equation the optimizer searches with), the phase summary must be
/// circular, and every number here is computed from the synthetic room in <see cref="OptimizationTestData"/>.
/// </summary>
public sealed class AbValidationTests(ITestOutputHelper output)
{
    /// <summary>The room: 6 positions × 40 bins, 5 Hz to 200 Hz.</summary>
    private static DualSubMeasurement Room => OptimizationTestData.Room();

    private static SubwooferSetting Setting => OptimizationTestData.Setting(1.5, 30.0);

    /// <summary>Applies a per-frequency complex factor to every bin, i.e. a hardware defect on the real pass.</summary>
    private static IReadOnlyList<PositionResponse> Defect(IReadOnlyList<PositionResponse> positions, Func<double, Complex> factor)
        => Defect(positions, (frequencyHz, _) => factor(frequencyHz));

    /// <summary>As above, with the bin index available (band splits must be exact, not frequency-guessed).</summary>
    private static IReadOnlyList<PositionResponse> Defect(IReadOnlyList<PositionResponse> positions, Func<double, int, Complex> factor)
        // Constructed through the validating constructor: with all three members get-only there is no `with` rewrite
        // that could build a position whose declared band does not describe its bins.
        => [.. positions.Select(position => new PositionResponse(position.PointId, position.AnalysisBand,
            [.. position.Bins.Select((bin, index) =>
            {
                Complex value = new Complex(bin.Real, bin.Imag) * factor(bin.FrequencyHz, index);
                return OptimizationTestData.Bin(bin.FrequencyHz, value.Real, value.Imaginary);
            })]))];

    [Fact]
    public void A_measured_AB_equal_to_the_prediction_agrees_exactly()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;

        // The "real" pass IS the model's own output, so predicted and measured are the same doubles: the
        // reported error is 0 dB and 0°, with no tolerance needed at all.
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);
        AbValidationResult result = AbValidation.Compare(
            room with { AB = predicted }, predicted, setting);

        Assert.Equal(AbValidationVerdict.Agrees, result.Verdict);
        Assert.Equal(0.0, result.Band.MaxAbsoluteMagnitudeErrorDb);
        Assert.Equal(0.0, result.Band.MaxAbsolutePhaseErrorDegrees);
        Assert.Equal(6 * 40, result.Band.FrequenciesCompared);              // 6 positions × 40 bins
        Assert.Equal(0, result.Band.DegenerateFrequencies);
        Assert.Empty(result.Checks);
        output.WriteLine($"agreement: max |Δ| = {result.Band.MaxAbsoluteMagnitudeErrorDb} dB, "
            + $"max |Δphase| = {result.Band.MaxAbsolutePhaseErrorDegrees}°");
    }

    [Fact]
    public void A_constant_level_error_of_2_5_db_is_reported_as_a_gain_problem()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);

        // Real pass 2.5 dB hot: 20·log10(10^(2.5/20)) = 2.5 dB, so every row's |error| is 2.5 dB and the
        // spread P90−P10 is 0 — the signature of a level offset rather than a filter.
        IReadOnlyList<PositionResponse> real = Defect(predicted, _ => new Complex(ComplexMathDb(2.5), 0));
        AbValidationResult result = AbValidation.Compare(room, real, setting);

        Assert.Equal(AbValidationVerdict.MagnitudeMismatch, result.Verdict);
        Assert.Equal(2.5, result.Band.P90AbsoluteMagnitudeErrorDb, 1e-12);
        Assert.Equal(-2.5, result.Band.MeanMagnitudeErrorDb, 1e-12);         // predicted − real
        Assert.Equal(0.0, result.Band.MagnitudeErrorSpreadDb, 1e-12);
        Assert.Equal([AbCheckKind.Gain], result.Checks);                     // and NOT device DSP or phase
        output.WriteLine($"gain case: mean {result.Band.MeanMagnitudeErrorDb:F12} dB, "
            + $"P90 |Δ| {result.Band.P90AbsoluteMagnitudeErrorDb:F12} dB, spread {result.Band.MagnitudeErrorSpreadDb:E1} dB");
    }

    [Fact]
    public void A_band_wide_180_degree_error_is_reported_as_polarity_not_as_a_phase_setting()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);

        // ×(−1): the magnitude is identical, the phase is inverted → Δphase = 180° at every row.
        IReadOnlyList<PositionResponse> real = Defect(predicted, _ => new Complex(-1.0, 0.0));
        AbValidationResult result = AbValidation.Compare(room, real, setting);

        Assert.Equal(AbValidationVerdict.PhaseMismatch, result.Verdict);
        Assert.Equal(180.0, Math.Abs(result.Band.CircularMeanPhaseErrorDegrees), 1e-9);
        Assert.Equal(0.0, result.Band.P90AbsoluteMagnitudeErrorDb, 1e-12);
        Assert.Contains(AbCheckKind.Polarity, result.Checks);
        Assert.DoesNotContain(AbCheckKind.PhaseSetting, result.Checks);      // 180° is a flip, not an offset
        Assert.Single(result.Checks);
    }

    [Fact]
    public void A_20_degree_rotation_is_reported_as_the_phase_setting()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);

        // exp(−j20°): the real pass lags the prediction by 20° → Δphase = +20° (within ±180°, so no wrapping).
        IReadOnlyList<PositionResponse> real = Defect(predicted, _ =>
            Complex.FromPolarCoordinates(1.0, -20.0 * Math.PI / 180.0));
        AbValidationResult result = AbValidation.Compare(room, real, setting);

        Assert.Equal(AbValidationVerdict.PhaseMismatch, result.Verdict);
        Assert.Equal(20.0, result.Band.CircularMeanPhaseErrorDegrees, 1e-9);
        Assert.Equal(0.0, result.Band.P90AbsoluteMagnitudeErrorDb, 1e-12);
        Assert.Equal([AbCheckKind.PhaseSetting], result.Checks);
    }

    [Fact]
    public void An_error_that_varies_with_frequency_points_at_dsp_on_one_path()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);

        // +3 dB only below 40 Hz (bins 5..35 Hz, 7 of 40 grid points): a narrowband defect. Over the whole
        // comparison that is 42 of 240 rows, so the mean offset is 42/240 × 3 dB = 0.525 dB — below the gain
        // threshold — while P90 |Δ| is 3 dB and the spread P90−P10 is 3 dB, i.e. the error is frequency-
        // dependent rather than a level offset.
        IReadOnlyList<PositionResponse> real = Defect(predicted, f =>
            f < 40.0 ? new Complex(ComplexMathDb(3.0), 0) : new Complex(1.0, 0));
        AbValidationResult result = AbValidation.Compare(room, real, setting);

        Assert.Equal(AbValidationVerdict.MagnitudeMismatch, result.Verdict);
        Assert.Equal(0.525, Math.Abs(result.Band.MeanMagnitudeErrorDb), 1e-12);   // 42/240 × 3 dB
        Assert.Equal(3.0, result.Band.P90AbsoluteMagnitudeErrorDb, 1e-12);
        Assert.Equal(3.0, result.Band.MagnitudeErrorSpreadDb, 1e-12);
        Assert.Equal([AbCheckKind.DeviceDsp], result.Checks);                   // not "gain": it is not constant
        output.WriteLine($"dsp case: mean {result.Band.MeanMagnitudeErrorDb:F12} dB, spread {result.Band.MagnitudeErrorSpreadDb:F12} dB");
    }

    [Fact]
    public void A_material_error_with_no_structured_cause_points_at_measurement_sync()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);

        // +1.8 dB over half the band: 120 of 240 rows at 1.8 dB, so mean 0.9 dB (under the gain threshold),
        // P90 |Δ| 1.8 dB (over the magnitude threshold) and spread 1.8 dB (under the frequency-dependence
        // threshold). No phase error. Nothing structural explains it, so the honest advice is to check the
        // capture itself rather than to invent a cause.
        IReadOnlyList<PositionResponse> real = Defect(predicted, f =>
            f >= 105.0 ? new Complex(ComplexMathDb(1.8), 0) : new Complex(1.0, 0));
        AbValidationResult result = AbValidation.Compare(room, real, setting);

        Assert.Equal(AbValidationVerdict.MagnitudeMismatch, result.Verdict);
        Assert.Equal(1.8, result.Band.P90AbsoluteMagnitudeErrorDb, 1e-12);
        Assert.Equal(0.9, Math.Abs(result.Band.MeanMagnitudeErrorDb), 1e-12);
        Assert.Equal(1.8, result.Band.MagnitudeErrorSpreadDb, 1e-12);
        Assert.Equal([AbCheckKind.MeasurementSync], result.Checks);
    }

    [Fact]
    public void The_phase_summary_is_circular_where_an_arithmetic_mean_would_report_zero()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);

        // Rotate alternating bins by +170° and −170°: an equal number of rows each way, so every row is 170°
        // wrong while the signed values cancel — the arithmetic mean reports 0° and hides the defect.
        IReadOnlyList<PositionResponse> real = Defect(predicted, (_, index) =>
            Complex.FromPolarCoordinates(1.0, (index % 2 == 0 ? 170.0 : -170.0) * Math.PI / 180.0));
        AbValidationResult result = AbValidation.Compare(room, real, setting);

        double[] wrapped = [.. result.Errors.Select(e => e.WrappedPhaseErrorDegrees)];
        Assert.Equal(0.0, wrapped.Average(), 1e-12);                          // arithmetic mean: 0° — misleading
        Assert.Equal(180.0, Math.Abs(result.Band.CircularMeanPhaseErrorDegrees), 1e-9);   // circular mean: ±180°
        Assert.Equal(170.0, result.Band.P90AbsolutePhaseErrorDegrees, 1e-9);
        Assert.Equal(AbValidationVerdict.PhaseMismatch, result.Verdict);
        output.WriteLine($"arithmetic {wrapped.Average():E3}° vs circular {result.Band.CircularMeanPhaseErrorDegrees:F9}°");
    }

    [Fact]
    public void The_thresholds_are_parameters_with_printed_defaults()
    {
        Assert.Equal(1.0, AbValidationOptions.Default.MagnitudeErrorThresholdDb);
        Assert.Equal(15.0, AbValidationOptions.Default.PhaseErrorThresholdDegrees);

        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);

        // The 2.5 dB defect that is a mismatch at the default 1.0 dB is agreement at a 3.0 dB threshold.
        IReadOnlyList<PositionResponse> hot = Defect(predicted, _ => new Complex(ComplexMathDb(2.5), 0));
        AbValidationResult loose = AbValidation.Compare(room, hot, setting, new AbValidationOptions(MagnitudeErrorThresholdDb: 3.0));
        Assert.Equal(AbValidationVerdict.Agrees, loose.Verdict);
        Assert.Equal(1.0, AbValidation.Compare(room, hot, setting).Options.MagnitudeErrorThresholdDb);   // default echoed

        // Same for phase: 20° is a mismatch at 15° and agreement at 25°.
        IReadOnlyList<PositionResponse> rotated = Defect(predicted, _ => Complex.FromPolarCoordinates(1.0, -20.0 * Math.PI / 180.0));
        Assert.Equal(AbValidationVerdict.Agrees,
            AbValidation.Compare(room, rotated, setting, new AbValidationOptions(PhaseErrorThresholdDegrees: 25.0)).Verdict);

        Assert.Throws<ArgumentOutOfRangeException>(() => AbValidation.Compare(room, rotated, setting, new AbValidationOptions(MagnitudeErrorThresholdDb: 0.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AbValidation.Compare(room, rotated, setting, new AbValidationOptions(PhaseErrorThresholdDegrees: 180.0)));
    }

    [Fact]
    public void The_band_percentiles_are_the_shared_nearest_rank_definition()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);
        IReadOnlyList<PositionResponse> real = Defect(predicted, f =>
            f >= 105.0 ? new Complex(ComplexMathDb(1.8), 0) : new Complex(1.0, 0));

        AbValidationResult result = AbValidation.Compare(room, real, setting);
        double[] errors = [.. result.Errors.Where(e => !e.Degenerate).Select(e => Math.Abs(e.MagnitudeErrorDb))];

        // PercentileNearestRank({…120 zeros, 120×1.8} , 90) = ceil(0.9·240) − 1 = index 215 of the sorted array.
        Assert.Equal(SpatialMetrics.PercentileNearestRank(errors, 90), result.Band.P90AbsoluteMagnitudeErrorDb);
        Assert.Equal(1.8, result.Band.P90AbsoluteMagnitudeErrorDb, 1e-12);
        Assert.Equal(
            SpatialMetrics.PercentileNearestRank(errors, 90) - SpatialMetrics.PercentileNearestRank(errors, 10),
            result.Band.MagnitudeErrorSpreadDb);
    }

    [Fact]
    public void A_measured_null_is_flagged_and_left_out_of_the_band_statistics()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);

        // One row reads a null of 1e-15, which is below the 1e-12 floor: its relative error is meaningless, so
        // it is flagged and excluded instead of dragging the summary to +100 dB.
        var bins = predicted[0].Bins.ToList();
        bins[3] = OptimizationTestData.Bin(20.0, 1e-15, 0.0);
        IReadOnlyList<PositionResponse> real =
            [new PositionResponse(predicted[0].PointId, predicted[0].AnalysisBand, bins), .. predicted.Skip(1)];
        AbValidationResult result = AbValidation.Compare(room, real, setting);

        Assert.Equal(1, result.Band.DegenerateFrequencies);
        Assert.Equal(6 * 40 - 1, result.Band.FrequenciesCompared);
        Assert.True(result.Errors.Single(e => e.FrequencyHz == 20.0 && e.Degenerate).MagnitudeErrorDb < double.PositiveInfinity);
        Assert.Equal(AbValidationVerdict.Agrees, result.Verdict);            // the other 239 rows are exact
    }

    [Fact]
    public void A_grid_mismatch_is_refused_rather_than_paired_by_index()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);

        IReadOnlyList<PositionResponse> renamed =
            [new PositionResponse("elsewhere", predicted[0].AnalysisBand, predicted[0].Bins), .. predicted.Skip(1)];
        ArgumentException byPosition = Assert.Throws<ArgumentException>(() => AbValidation.Compare(room, renamed, setting));
        Assert.Contains("elsewhere", byPosition.Message);

        var shifted = predicted[0].Bins.ToList();
        shifted[5] = OptimizationTestData.Bin(999.0, 1.0, 0.0);
        // 999 Hz is outside the declared band, so the constructor refuses it before AbValidation is reached: the band is
        // the first line of defence. Exact type: ArgumentOutOfRangeException is a subclass, not an ArgumentException.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PositionResponse(predicted[0].PointId, predicted[0].AnalysisBand, shifted));

        // An in-band frequency on a different grid gets past the constructor and must be refused by AbValidation itself.
        shifted[5] = OptimizationTestData.Bin(20.0, 1.0, 0.0);
        ArgumentException byFrequency = Assert.Throws<ArgumentException>(() =>
        {
            IReadOnlyList<PositionResponse> otherGrid =
                [new PositionResponse(predicted[0].PointId, predicted[0].AnalysisBand, shifted), .. predicted.Skip(1)];
            AbValidation.Compare(room, otherGrid, setting);
        });
        // The message reads predicted-then-measured, so index 5 is 30 Hz (the grid is 5+5k) against the 20 Hz we set.
        Assert.Contains("bin 5 is 30 Hz predicted and 20 Hz measured", byFrequency.Message);
    }

    [Fact]
    public void The_prediction_comes_from_the_model_not_from_the_measured_AB_field()
    {
        DualSubMeasurement room = Room;
        SubwooferSetting setting = Setting;
        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(room, setting);

        // The measurement carries a measured AB that is NOT this setting's output. A validator that read that
        // field back would agree; one that calls the model reports the real error.
        IReadOnlyList<PositionResponse> real = Defect(predicted, _ => new Complex(ComplexMathDb(2.5), 0));
        SubwooferSetting different = OptimizationTestData.Setting(0.0, 0.0);
        AbValidationResult result = AbValidation.Compare(room with { AB = real }, real, different);

        Assert.NotEqual(AbValidationVerdict.Agrees, result.Verdict);
        IReadOnlyList<PositionResponse> model = SubwooferModel.Combine(room, different);
        for (int i = 0; i < result.Errors.Count; i++)
        {
            FrequencyResponse expected = model[i / 40].Bins[i % 40];
            Assert.Equal(20.0 * Math.Log10(new Complex(expected.Real, expected.Imag).Magnitude), result.Errors[i].PredictedMagnitudeDb, 1e-12);
        }
    }

    private static double ComplexMathDb(double decibels) => Math.Pow(10.0, decibels / 20.0);
}
