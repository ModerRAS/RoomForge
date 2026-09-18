namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.IO;
using AudioOptimizer.Visualization;
using Xunit;

/// <summary>
/// §27 microphone calibration: the file format, the correction curve, and the rule that an absolute reference can
/// only be claimed by data that carries one. Every assertion carries its formula and expected value.
/// </summary>
public sealed class CalibrationTests
{
    /// <summary>
    /// A real-shaped UMIK-1 file: a quoted header, the quoted Sens Factor/SERNO metadata line, a comment, a
    /// <c>Sensitivity</c> line, tab-separated rows, and <b>deliberately unsorted</b> rows — the format does not
    /// promise an order, so nothing here may assume one.
    /// </summary>
    private static readonly string RealShaped = string.Join('\n',
        "\"Auto-generated 90-degree calibration file\"",
        "\"Sens Factor =-0.0109999dB, SERNO: 7187696\"",
        "* miniDSP calibration export",
        "Sensitivity = 94.0 dBFS",
        "1000.0\t2.0",
        "20000.0\t0.25",
        "# the row order below is deliberately not ascending",
        "100.0\t-1.0",
        "20.0\t-3.5",
        "");

    /// <summary>The same four points, ascending — the input the unsorted file must be equivalent to.</summary>
    private static readonly string Sorted = string.Join('\n',
        "\"Sens Factor =-0.0109999dB, SERNO: 7187696\"",
        "Sensitivity = 94.0 dBFS",
        "20.0\t-3.5",
        "100.0\t-1.0",
        "1000.0\t2.0",
        "20000.0\t0.25",
        "");

    [Fact]
    public void A_real_shaped_file_parses_and_is_sorted_internally()
    {
        CalibrationParseResult result = CalibrationFileParser.Parse(RealShaped);

        Assert.True(result.Succeeded, result.Message);
        MicrophoneCalibration calibration = result.Calibration!;
        Assert.Equal("7187696", calibration.Identity);                          // the file's own SERNO: line
        Assert.Equal(94.0, calibration.SensitivityDbSplPerFullScale, 12);       // 'Sensitivity = 94.0 dBFS'
        Assert.Equal([20.0, 100.0, 1000.0, 20000.0], calibration.Points.Select(point => point.FrequencyHz));
        Assert.Equal([-3.5, -1.0, 2.0, 0.25], calibration.Points.Select(point => point.CorrectionDb));
        Assert.Equal((20.0, 20000.0), calibration.CalibratedExtent);
    }

    [Fact]
    public void Unsorted_input_gives_the_same_result_as_sorted()
    {
        MicrophoneCalibration fromUnsorted = CalibrationFileParser.Parse(RealShaped).Calibration!;
        MicrophoneCalibration fromSorted = CalibrationFileParser.Parse(Sorted).Calibration!;

        Assert.Equal(fromSorted.Points, fromUnsorted.Points);
        // Same correction everywhere, including at a frequency that lands inside the first span.
        foreach (double frequency in new[] { 20.0, 60.0, 100.0, 550.0, 1000.0, 20000.0 })
            Assert.Equal(fromSorted.CorrectionAt(frequency).CorrectionDb, fromUnsorted.CorrectionAt(frequency).CorrectionDb, 12);
    }

    [Fact]
    public void The_log_frequency_midpoint_is_the_arithmetic_mean_of_the_dB_values()
    {
        var calibration = new MicrophoneCalibration("two-point.txt", 94.0,
            [new CalibrationPoint(100.0, -1.0), new CalibrationPoint(1000.0, 2.0)]);

        // Geometric mean of the two points: sqrt(100·1000) = 316.22776601683794 Hz.
        // t = ln(316.22776601683794 / 100) / ln(1000 / 100) = 0.5 exactly, so the correction is
        // −1.0 + 0.5·(2.0 − (−1.0)) = +0.5 dB.
        CalibrationCorrection midpoint = calibration.CorrectionAt(316.22776601683794);
        Assert.True(midpoint.IsCalibrated);
        Assert.Equal(0.5, midpoint.CorrectionDb, 1e-12);

        // A LINEAR-in-frequency interpolation puts the midpoint at (100+1000)/2 = 550 Hz: its t would be
        // (316.22776601683794 − 100)/(1000 − 100) = 0.2402530733 and it would report
        // −1.0 + 0.2402530733·3 = −0.2792407799 dB. That number is what a wrong implementation produces, and the
        // gap to +0.5 dB is what makes this test discriminating rather than merely arithmetic.
        Assert.True(Math.Abs(midpoint.CorrectionDb - (-0.2792407799)) > 0.7, $"log midpoint {midpoint.CorrectionDb} vs linear −0.2792407799");

        // Exact hits return the point's own value, not an interpolated approximation of it.
        Assert.Equal(-1.0, calibration.CorrectionAt(100.0).CorrectionDb);
        Assert.Equal(2.0, calibration.CorrectionAt(1000.0).CorrectionDb);
    }

    [Fact]
    public void A_frequency_outside_the_files_range_is_uncalibrated_and_not_extrapolated()
    {
        MicrophoneCalibration calibration = CalibrationFileParser.Parse(RealShaped).Calibration!;

        // 10 Hz is below the first point (20 Hz) and 24 kHz above the last (20 kHz): both uncalibrated, neither
        // carrying an invented value. The flag is the discriminating assertion — a 0 dB correction inside the range
        // and 0 dB outside are different facts.
        Assert.False(calibration.CorrectionAt(10.0).IsCalibrated);
        Assert.False(calibration.CorrectionAt(24000.0).IsCalibrated);
        Assert.Equal(0.0, calibration.CorrectionAt(10.0).CorrectionDb);

        // Inclusive at both edges, like FrequencyBand.Contains, because both edges were measured.
        Assert.True(calibration.CorrectionAt(20.0).IsCalibrated);
        Assert.True(calibration.CorrectionAt(20000.0).IsCalibrated);
        Assert.Equal(-3.5, calibration.CorrectionAt(20.0).CorrectionDb);
        Assert.Equal(0.25, calibration.CorrectionAt(20000.0).CorrectionDb);

        Assert.False(calibration.CoversFrequency(19.999));
        Assert.False(calibration.CoversFrequency(0.0));
        Assert.False(calibration.CoversFrequency(double.NaN));
        Assert.True(calibration.CoversFrequency(550.0));
    }

    [Fact]
    public void A_zero_decibel_correction_inside_the_range_is_still_calibrated()
    {
        var flat = new MicrophoneCalibration("flat.txt", 94.0,
            [new CalibrationPoint(20.0, 0.0), new CalibrationPoint(20000.0, 0.0)]);

        // Corrected by 0 dB is not the same fact as not calibrated here — the whole reason the accessor returns a
        // shape with a flag instead of a nullable double.
        CalibrationCorrection inside = flat.CorrectionAt(1000.0);
        Assert.True(inside.IsCalibrated);
        Assert.Equal(0.0, inside.CorrectionDb, 12);
        Assert.False(flat.CorrectionAt(10.0).IsCalibrated);

        // And the default value reads as uncalibrated, so a forgotten branch cannot look like a free 0 dB.
        Assert.False(default(CalibrationCorrection).IsCalibrated);

        // A calibration with no points at all is absolute-only: the sensitivity still labels, the shape does not.
        var absoluteOnly = new MicrophoneCalibration("sensitivity-only.txt", 94.0);
        Assert.Null(absoluteOnly.CalibratedExtent);
        Assert.False(absoluteOnly.CorrectionAt(1000.0).IsCalibrated);
        Assert.Contains("no correction points", LevelReference.SplCalibrated(absoluteOnly).AxisLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_rows_are_refused_with_the_line_that_says_so()
    {
        // Line 4 is the bad one: a frequency with no parsable correction in column 1.
        const string malformed = "SERNO: 7187696\nSensitivity = 94.0 dBFS\n20.0 -3.5\n100.0 notanumber\n";
        CalibrationParseResult result = CalibrationFileParser.Parse(malformed);

        Assert.False(result.Succeeded);
        Assert.Equal(4, result.Error!.LineNumber);
        Assert.Equal("100.0 notanumber", result.Error.Line);
        Assert.Contains("column 1", result.Error.Reason, StringComparison.Ordinal);
        Assert.Equal("Line 4: a data row needs a correction in column 1", result.Message);

        // A line that is neither a row nor a recognised header is refused too, never silently skipped: line 3.
        CalibrationParseResult stray = CalibrationFileParser.Parse("SERNO: 1\nSensitivity = 94.0 dBFS\njust some prose\n");
        Assert.False(stray.Succeeded);
        Assert.Equal(3, stray.Error!.LineNumber);
        Assert.Equal("just some prose", stray.Error.Line);

        // Duplicate frequencies name the line that repeats, because the correction there would be ambiguous.
        CalibrationParseResult duplicates = CalibrationFileParser.Parse("SERNO: 1\nSensitivity = 94.0 dBFS\n20.0 -1.0\n20.0 -2.0\n");
        Assert.False(duplicates.Succeeded);
        Assert.Equal(4, duplicates.Error!.LineNumber);
        Assert.Contains("ambiguous", duplicates.Error.Reason, StringComparison.Ordinal);

        // A file with no rows at all is a whole-file problem (line 0), not a bad line.
        CalibrationParseResult empty = CalibrationFileParser.Parse("\"Auto-generated calibration file\"\n* nothing else\n");
        Assert.False(empty.Succeeded);
        Assert.Equal(0, empty.Error!.LineNumber);
        Assert.Contains("no '<frequency> <dB>' rows", empty.Error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sens_factor_line_is_not_an_absolute_sensitivity()
    {
        // The real file's own metadata: 'Sens Factor =-0.0109999dB' is a hundredth of a dB, a shape factor, not an
        // SPL-per-full-scale reference. Treating it as the sensitivity would turn "SPL" into a claim the file does
        // not support, so with no 'Sensitivity =' line and nothing supplied, there is no payload at all.
        const string shapeOnly = "\"Sens Factor =-0.0109999dB, SERNO: 7187696\"\n20.0\t-3.5\n1000.0\t0.5\n";

        CalibrationParseResult absent = CalibrationFileParser.Parse(shapeOnly);
        Assert.False(absent.Succeeded);
        Assert.Equal(0, absent.Error!.LineNumber);                  // a whole-file problem, not one line
        Assert.Contains("no absolute sensitivity", absent.Error.Reason, StringComparison.Ordinal);

        // Supplied explicitly, the same file does build a payload, and the label moves from relative to SPL —
        // the before/after pair the ruling asks for.
        CalibrationParseResult supplied = CalibrationFileParser.Parse(shapeOnly, sensitivityDbSplPerFullScale: 94.0);
        Assert.True(supplied.Succeeded, supplied.Message);
        MicrophoneCalibration payload = supplied.Calibration!;

        string relative = LevelReference.RelativeToBandMean.AxisLabel;
        string spl = LevelReference.SplCalibrated(payload).AxisLabel;
        Assert.DoesNotContain("SPL", relative, StringComparison.Ordinal);
        Assert.Contains("SPL", spl, StringComparison.Ordinal);
        Assert.Contains("7187696", spl, StringComparison.Ordinal);
        Assert.Contains("94.0", spl, StringComparison.Ordinal);
        Assert.Contains("20-1000 Hz calibrated", spl, StringComparison.Ordinal);      // the ACHIEVED extent, not the wished-for one
        Assert.Equal(94.0, payload.SensitivityDbSplPerFullScale, 12);
    }

    [Fact]
    public void The_type_refuses_what_it_cannot_represent()
    {
        Assert.Throws<ArgumentException>(() => new MicrophoneCalibration("  ", 94.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MicrophoneCalibration("cal.txt", double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MicrophoneCalibration("cal.txt", 94.0, [new CalibrationPoint(0.0, 1.0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MicrophoneCalibration("cal.txt", 94.0, [new CalibrationPoint(20.0, double.PositiveInfinity)]));
        Assert.Throws<ArgumentException>(() => new MicrophoneCalibration("cal.txt", 94.0, [new CalibrationPoint(20.0, 0.0), new CalibrationPoint(20.0, 1.0)]));

        // Ordering is the type's job, not the caller's: a descending pair is sorted, not rejected.
        var sortedInternally = new MicrophoneCalibration("cal.txt", 94.0,
            [new CalibrationPoint(1000.0, 2.0), new CalibrationPoint(100.0, -1.0)]);
        Assert.Equal([100.0, 1000.0], sortedInternally.Points.Select(point => point.FrequencyHz));
    }
}
