using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Optimization;

namespace AudioOptimizer.Tests;

/// <summary>
/// Model identities. The two things this file exists to keep honest: the sum is a complex sum (dB addition is
/// forbidden), and a frequency-independent phase is not a delay.
/// </summary>
public class SubwooferModelTests
{
    private static readonly DualSubMeasurement TwoUnitSubs =
        new([OptimizationTestData.OneBin(50.0, 1.0, 0.0)], [OptimizationTestData.OneBin(50.0, 1.0, 0.0)]);

    [Fact]
    public void TwoIdenticalSubsInPhaseAddToPlus6_0206Db()
    {
        double magnitude = OptimizationTestData.TotalMagnitude(TwoUnitSubs, OptimizationTestData.Setting(0.0, 0.0));
        // |H_A + H_B| = |1 + 1| = 2 → 20·log10(2) = 6.020599913279624 dB
        Assert.Equal(2.0, magnitude, 12);
        Assert.Equal(6.020599913279624, 20.0 * Math.Log10(magnitude), 12);
    }

    [Fact]
    public void TwoIdenticalSubsNinetyDegreesApartAddToPlus3_0103Db()
    {
        double magnitude = OptimizationTestData.TotalMagnitude(TwoUnitSubs, OptimizationTestData.Setting(0.0, 90.0));
        // |1 + exp(j90°)| = |1 + j| = √2 → 20·log10(√2) = 3.010299956639812 dB
        Assert.Equal(Math.Sqrt(2.0), magnitude, 12);
        Assert.Equal(3.010299956639812, 20.0 * Math.Log10(magnitude), 12);
    }

    [Fact]
    public void TwoIdenticalSubsOppositePhaseCancelAndDoNotAddInDb()
    {
        double magnitude = OptimizationTestData.TotalMagnitude(TwoUnitSubs, OptimizationTestData.Setting(0.0, 180.0));
        // |1 + (−1)| = 0: exact cancellation. Adding dB would give 0 dB + 0 dB → +6.02 dB, i.e. |1 + 1| = 2.
        Assert.True(magnitude < 1e-12, $"expected cancellation < 1e-12, measured |H| = {magnitude}");
        Assert.NotEqual(2.0, magnitude);
    }

    [Fact]
    public void PolarityFlipIsTheSameAsOneHundredEightyDegrees()
    {
        double flipped = OptimizationTestData.TotalMagnitude(TwoUnitSubs, OptimizationTestData.Setting(0.0, 0.0, polarity: -1));
        double rotated = OptimizationTestData.TotalMagnitude(TwoUnitSubs, OptimizationTestData.Setting(0.0, 180.0));
        // −1 = exp(j180°), so the two settings are the same complex multiplication.
        Assert.True(Math.Abs(flipped - rotated) < 1e-12, $"flipped {flipped} vs rotated {rotated}");
    }

    [Fact]
    public void GainIsDbToLinearAmplitude()
    {
        var measurement = new DualSubMeasurement(
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0)],
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0)]);
        // +6.020599913279624 dB → ×2 amplitude, so the total is |1 + 2| = 3 → 20·log10(3) = 9.542425094393248 dB.
        double magnitude = OptimizationTestData.TotalMagnitude(measurement, OptimizationTestData.Setting(6.020599913279624, 0.0));
        Assert.Equal(3.0, magnitude, 12);
        Assert.Equal(9.542425094393248, 20.0 * Math.Log10(magnitude), 12);
    }

    [Fact]
    public void DelayIsFrequencyProportionalAndPhaseIsNot()
    {
        var oneAt50 = new DualSubMeasurement([OptimizationTestData.OneBin(50.0, 1.0, 0.0)], [OptimizationTestData.OneBin(50.0, 1.0, 0.0)]);
        var oneAt100 = new DualSubMeasurement([OptimizationTestData.OneBin(100.0, 1.0, 0.0)], [OptimizationTestData.OneBin(100.0, 1.0, 0.0)]);

        // A 5 ms delay is exp(−j2πf·0.005): −90° at 50 Hz, −180° at 100 Hz.
        FrequencyResponse byPhase = SubwooferModel.Combine(oneAt50, OptimizationTestData.Setting(0.0, -90.0))[0].Bins[0];
        FrequencyResponse byDelay = SubwooferModel.Combine(oneAt50, OptimizationTestData.Setting(0.0, 0.0, delayMilliseconds: 5.0))[0].Bins[0];
        Assert.Equal(byPhase.Real, byDelay.Real, 12);
        Assert.Equal(byPhase.Imag, byDelay.Imag, 12);
        Assert.Equal(-1.0, byDelay.Imag, 12); // 1 + (−j) → the imaginary part is exactly −1

        // Same phase, different delay: φ = −90° is 5 ms at 50 Hz but 2.5 ms at 100 Hz, so it cannot be a delay.
        double at50Phase = OptimizationTestData.TotalMagnitude(oneAt50, OptimizationTestData.Setting(0.0, -90.0));
        double at50Delay = OptimizationTestData.TotalMagnitude(oneAt50, OptimizationTestData.Setting(0.0, 0.0, delayMilliseconds: 5.0));
        double at100Phase = OptimizationTestData.TotalMagnitude(oneAt100, OptimizationTestData.Setting(0.0, -90.0));
        double at100Delay = OptimizationTestData.TotalMagnitude(oneAt100, OptimizationTestData.Setting(0.0, 0.0, delayMilliseconds: 5.0));

        Assert.Equal(Math.Sqrt(2.0), at50Phase, 12);
        Assert.Equal(at50Phase, at50Delay, 12);       // the same fixed phase and a 5 ms delay agree at 50 Hz
        Assert.Equal(Math.Sqrt(2.0), at100Phase, 12); // |1 − j| = √2
        Assert.True(at100Delay < 1e-12, $"5 ms at 100 Hz is −180°, so it cancels: measured {at100Delay}");
        Assert.NotEqual(at100Phase, at100Delay);      // and they disagree at 100 Hz — phase ≠ delay
    }

    [Fact]
    public void PhaseAndDelayAreSeparateParametersWithSeparateEffects()
    {
        var measurement = new DualSubMeasurement(
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0), OptimizationTestData.OneBin(100.0, 1.0, 0.0)],
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0), OptimizationTestData.OneBin(100.0, 1.0, 0.0)]);

        var byPhase = SubwooferModel.Combine(measurement, OptimizationTestData.Setting(0.0, -90.0));
        var byDelay = SubwooferModel.Combine(measurement, OptimizationTestData.Setting(0.0, 0.0, delayMilliseconds: 5.0));

        // Both give 1 − j at 50 Hz, but at 100 Hz the phase gives 1 − j while the 5 ms delay gives 1 − 1 = 0.
        Assert.Equal(byPhase[0].Bins[0].Real, byDelay[0].Bins[0].Real, 12);
        Assert.Equal(byPhase[0].Bins[0].Imag, byDelay[0].Bins[0].Imag, 12);
        Assert.Equal(Math.Sqrt(2.0), Math.Sqrt(byPhase[1].Bins[0].Real * byPhase[1].Bins[0].Real + byPhase[1].Bins[0].Imag * byPhase[1].Bins[0].Imag), 12);
        Assert.True(Math.Sqrt(byDelay[1].Bins[0].Real * byDelay[1].Bins[0].Real + byDelay[1].Bins[0].Imag * byDelay[1].Bins[0].Imag) < 1e-12);
    }

    [Fact]
    public void NothingAssumesTwentySevenPositions()
    {
        // Two positions, three bins: the model sums per position per bin and never indexes a fixed grid.
        var a = new List<PositionResponse>
        {
            new("p0", OptimizationTestData.Band, [OptimizationTestData.Bin(10.0, 1.0, 0.0), OptimizationTestData.Bin(20.0, 0.5, 0.0), OptimizationTestData.Bin(30.0, 0.25, 0.0)]),
            new("p1", OptimizationTestData.Band, [OptimizationTestData.Bin(10.0, 0.0, 1.0), OptimizationTestData.Bin(20.0, 0.5, 0.0), OptimizationTestData.Bin(30.0, 0.0, 0.25)]),
        };
        var b = new List<PositionResponse>
        {
            new("p0", OptimizationTestData.Band, [OptimizationTestData.Bin(10.0, 1.0, 0.0), OptimizationTestData.Bin(20.0, 0.5, 0.0), OptimizationTestData.Bin(30.0, 0.25, 0.0)]),
            new("p1", OptimizationTestData.Band, [OptimizationTestData.Bin(10.0, 0.0, 1.0), OptimizationTestData.Bin(20.0, 0.5, 0.0), OptimizationTestData.Bin(30.0, 0.0, 0.25)]),
        };

        IReadOnlyList<PositionResponse> totals = SubwooferModel.Combine(new DualSubMeasurement(a, b), OptimizationTestData.Setting(0.0, 0.0));
        Assert.Equal(2, totals.Count);
        Assert.Equal(3, totals[0].Bins.Count);
        Assert.Equal(2.0, Math.Sqrt(totals[0].Bins[0].Real * totals[0].Bins[0].Real + totals[0].Bins[0].Imag * totals[0].Bins[0].Imag), 12);
    }

    [Fact]
    public void TheInputContractAcceptsTheMeasuredAbPass()
    {
        DualSubMeasurement measurement = OptimizationTestData.Room(3, 4) with { AB = OptimizationTestData.Room(3, 4).A };
        measurement.Validate();
        // AB is carried so real session data plugs in; the model predicts from A and B rather than reading it back.
        Assert.Equal(3, SubwooferModel.Combine(measurement, OptimizationTestData.Setting(0.0, 0.0)).Count);
    }

    [Fact]
    public void MismatchedPositionsAndFrequenciesAreRejected()
    {
        var twoBins = new DualSubMeasurement(
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0)],
            [new("p0", OptimizationTestData.Band, [OptimizationTestData.Bin(50.0, 1.0, 0.0), OptimizationTestData.Bin(60.0, 1.0, 0.0)])]);
        Assert.Throws<ArgumentException>(() => twoBins.Validate());

        var twoPositions = new DualSubMeasurement(
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0), OptimizationTestData.OneBin(60.0, 1.0, 0.0)],
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0)]);
        Assert.Throws<ArgumentException>(() => twoPositions.Validate());

        var wrongFrequencies = new DualSubMeasurement(
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0)],
            [OptimizationTestData.OneBin(60.0, 1.0, 0.0)]);
        Assert.Throws<ArgumentException>(() => wrongFrequencies.Validate());
    }

    [Fact]
    public void SettingsMustBePhysicallyMeaningful()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OptimizationTestData.Setting(0.0, 0.0, polarity: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => OptimizationTestData.Setting(0.0, 0.0, delayMilliseconds: -1.0).Validate());
        OptimizationTestData.Setting(0.0, 0.0, polarity: -1).Validate();
        Assert.Equal(-1, OptimizationTestData.Setting(0.0, 0.0).Flipped.Polarity);
    }

    [Fact]
    public void QuantisingSnapsToHardwareHoldableSteps()
    {
        // 3.04 dB / 39.4° → the nearest 0.1 dB / 1° the hardware can hold: 3.0 / 39.0.
        SubwooferSetting snapped = OptimizationTestData.Setting(3.04, 39.4).Quantized(0.1, 1.0);
        Assert.Equal(3.0, snapped.GainDb, 12);
        Assert.Equal(39.0, snapped.PhaseDegrees, 9);
        Assert.Equal(3.04, OptimizationTestData.Setting(3.04, 39.4).GainDb, 12);
    }
}
