namespace AudioOptimizer.Tests.Adversarial;

using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Optimization;

/// <summary>
/// Worker C of the adversarial wave: phase / delay / polarity attacks on the SHIPPED search, through the real
/// pipeline. The heavy sweeps live in a scratch console outside the repo; these are the fast, deterministic
/// pinned cases they produced, plus the acoustic identities the attack rests on.
/// <para>
/// Two defects found by this attack, both category 4 (optimizer), both fixed in the wave they were found:
/// polarity is now searched on both sides of the [0, 180] phase split inside the joint sweep, and a
/// non-finite candidate score is mapped to +inf instead of being compared. The tests below pin the FIXED
/// behaviour; the comment above each records the pre-fix numbers the fixture produced.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>PD-1 — polarity is locked before the joint sweep.</b> <c>SubwooferSweeper.SweepPolarity</c> runs once at
/// the measured setting; <c>CoarseJointSweep</c> and <c>LocalJointRefine</c> then keep the incumbent's
/// polarity. Since phase is searched over [0, 180] only, a locked polarity can reach just half of the drive
/// rotation circle: +1 covers [0, 180], -1 covers [180, 360]. When the acoustically best rotation is on the
/// other side of the split — e.g. the misaligned B needs a correction of -90 deg, which is only expressible as
/// polarity -1 at +90 deg — the search cannot reach it at all. Real-pipeline replay: A=(0.45,0.45,0.35) 0
/// dB/+1/0 deg/0 s; B=(2.85,3.15,0.35) 0 dB/+1/+90 deg/0 s; 27 mics (ListeningRegion.Default), room
/// 3.3x3.6x2.6 m, ISM order 3, sweep 20-150 Hz, MaxBoostLimitDb 30, IncludeDelay true: the search never leaves
/// the measured setting (score 26.336, mean sigma 3.923 dB) while a both-polarity scan reaches score 23.778
/// (sigma 2.141 dB). The fixture below is the same defect in a 4-position analytic measurement.
/// </description></item>
/// <item><description>
/// <b>PD-2 — an exactly cancelling candidate produces a NaN score and derails the score order.</b> A candidate
/// whose total is exactly zero at one bin has a level of -inf dB, so <c>SpatialMetrics.Compute</c> returns
/// NaN (stddev of (-inf - (-inf)), worstNull of (-inf - (-inf))). <c>SubwooferOptimizer.IsBetter</c> then
/// falls through its two score comparisons (both false for NaN) to the distance tie-break, so a NaN candidate
/// closer to the measured setting replaces a finite incumbent regardless of score, and the rest of the search
/// descends by distance-to-measured-setting instead of by score. Real-pipeline replay: the same rig with B at
/// +150 deg and the optimizer fed <c>VirtualLab.GroundTruth.AsOptimizerInput()</c> (9 mics): the wide
/// (PhaseMaxDegrees 350 deg) search returns score 55.95 where the same search at B=+149.9 deg returns 27.90.
/// The measured path (with its -120 dBFS noise) has no exact-zero bin (min |A| = 3.1e-2, min |A+B| = 4.4e-3),
/// so production exposure is low; the simulator's own ground-truth input shape is where it fires.
/// </description></item>
/// </list>
/// </summary>
public class PhaseDelayAttackTests
{
    private const double MaxBoostDb = 30.0;

    private static readonly OptimizerOptions SearchOptions = new() { MaxBoostLimitDb = MaxBoostDb };

    /// <summary>Both polarities, 0.5 dB x 2 deg: the acoustic reference the search is measured against.</summary>
    private static readonly OptimizerOptions ReferenceOptions = new()
    {
        MaxBoostLimitDb = MaxBoostDb,
        GainCoarseStepDb = 0.5,
        PhaseCoarseStepDegrees = 2.0,
    };

    /// <summary>
    /// PD-1 fixture (4 positions x 3 bins, 40/70/100 Hz): the shipped search scores 25.8663 and a
    /// both-polarity brute force 17.3286. Derived in the scratch sweep; values are the fixture, not physics.
    /// </summary>
    private static readonly double[][] F1A =
    [
        [-0.433840, -0.138686], [-0.892220, -0.141827], [-0.004269, 0.415793],
        [-0.860007, -0.088839], [0.026593, -0.965629], [-1.140143, -0.228162],
        [1.234672, 0.253242], [0.736118, 0.029103], [0.810921, -0.805579],
        [0.688184, -0.197962], [0.418612, -0.015622], [-1.321125, 0.154243],
    ];

    private static readonly double[][] F1B =
    [
        [-0.191630, -0.630691], [0.732250, 0.457327], [-0.590678, -0.280453],
        [0.480199, -0.195507], [-0.418672, 1.184790], [-0.362170, 0.115515],
        [1.187761, 0.078563], [0.771527, 0.209530], [0.258457, -0.388008],
        [0.283020, 0.135051], [-0.797881, 0.012307], [1.194459, -0.091915],
    ];

    /// <summary>
    /// PD-2 fixture: same shape as <see cref="F1A"/>/<see cref="F1B"/>, except position 0 / bin 0 has A
    /// replaced at test time by -e^{j20 deg} * B, so setting (0 dB, +1, 20 deg, 0 s) cancels exactly.
    /// The shipped search scores 38.7272; the both-polarity reference scores 14.9047.
    /// </summary>
    private static readonly double[][] F2A =
    [
        [-0.321338486, -0.231476590], [-0.731511543, -0.452882145], [-0.850909903, -0.559340072],
        [-0.187463343, -1.480826778], [-0.095747357, -0.710648068], [0.489391098, -0.520470043],
        [0.654485612, 0.342188886], [0.180642692, -1.173723280], [0.383685345, -1.327941475],
        [0.152566684, -0.262654592], [0.158936091, 1.273615628], [0.295134779, 0.168997004],
    ];

    private static readonly double[][] F2B =
    [
        [0.381129060, 0.107612608], [0.181103393, -0.680970470], [-0.278722309, -0.943837763],
        [-0.484265021, 0.889973825], [1.116785582, 0.446608314], [0.177433937, 0.618554017],
        [-0.088836130, 0.379982661], [0.286154117, 1.398485208], [0.002288888, 1.459343713],
        [0.409326676, -0.877261249], [-0.148355085, -0.460998793], [1.111062986, 0.700690043],
    ];

    /// <summary>
    /// The equivalence class the attack plan asks to document: flipping polarity and adding a half turn is the
    /// SAME drive, at every frequency and for any delay. The optimizer may return either spelling; the two are
    /// acoustically identical, so this is category 6 (multiple valid solutions), not an inconsistency.
    /// </summary>
    [Fact]
    public void AHalfTurnReparameterisationIsAcousticallyExact()
    {
        DualSubMeasurement measurement = Fixture(F1A, F1B);
        IReadOnlyList<PositionResponse> baseline = SubwooferModel.Combine(measurement, SubwooferSetting.Baseline);

        foreach (double phaseDegrees in new[] { 0.0, 40.0, 90.0, 135.0, 180.0 })
        {
            SubwooferSetting positive = SubwooferSetting.FromDegrees(-2.5, phaseDegrees, 1, 0.004);
            SubwooferSetting negative = SubwooferSetting.FromDegrees(-2.5, phaseDegrees + 180.0, -1, 0.004);

            SpatialSummary a = SpatialMetrics.Compute(SubwooferModel.Combine(measurement, positive));
            SpatialSummary b = SpatialMetrics.Compute(SubwooferModel.Combine(measurement, negative));

            Assert.Equal(a.MeanStdDevDb, b.MeanStdDevDb, 12);
            Assert.Equal(a.MeanP90P10Db, b.MeanP90P10Db, 12);
            Assert.Equal(a.WorstNullDb, b.WorstNullDb, 12);
            Assert.Equal(a.MaxPeakAboveMeanDb, b.MaxPeakAboveMeanDb, 12);
            Assert.Equal(a.PerFrequency.Count, b.PerFrequency.Count);
            for (int k = 0; k < a.PerFrequency.Count; k++)
                Assert.Equal(a.PerFrequency[k].MeanDb, b.PerFrequency[k].MeanDb, 12);

            double boostA = ObjectiveFunction.MaxBoostVsBaselineDb(SubwooferModel.Combine(measurement, positive), baseline);
            double boostB = ObjectiveFunction.MaxBoostVsBaselineDb(SubwooferModel.Combine(measurement, negative), baseline);
            Assert.Equal(boostA, boostB, 12);
        }
    }

    /// <summary>
    /// The other half of the separation the brief asks about: phase is exp(j*phi) at every frequency; delay is
    /// exp(-j*2*pi*f*dt). A 5 ms delay and the fixed phase that matches it at 40 Hz (72 deg) must NOT be
    /// interchangeable. Reference (F1 fixture, 0 dB, +1): delay 5 ms gives sigma 2.9892 / P90-P10 7.7683 /
    /// worstNull 3.8514 dB; phase 72 deg gives sigma 4.5693 / P90-P10 11.2862 / worstNull 11.6657 dB. A model
    /// that collapsed one into the other would move these numbers together.
    /// </summary>
    [Fact]
    public void ADelayIsNotAFixedPhaseAtTheBandsCentre()
    {
        DualSubMeasurement measurement = Fixture(F1A, F1B);

        SpatialSummary delay = SpatialMetrics.Compute(
            SubwooferModel.Combine(measurement, SubwooferSetting.FromDegrees(0.0, 0.0, 1, 0.005)));
        SpatialSummary phase = SpatialMetrics.Compute(
            SubwooferModel.Combine(measurement, SubwooferSetting.FromDegrees(0.0, 72.0, 1, 0.0)));

        Assert.Equal(2.9892, delay.MeanStdDevDb, 4);
        Assert.Equal(7.7683, delay.MeanP90P10Db, 4);
        Assert.Equal(3.8514, delay.WorstNullDb, 4);
        Assert.Equal(4.5693, phase.MeanStdDevDb, 4);
        Assert.Equal(11.2862, phase.MeanP90P10Db, 4);
        Assert.Equal(11.6657, phase.WorstNullDb, 4);
        Assert.True(phase.MeanStdDevDb - delay.MeanStdDevDb > 1.0,
            "the delay and the fixed phase it matches at 40 Hz must not be interchangeable over the band");
    }

    /// <summary>
    /// PD-1 (fixed): the joint sweep now covers BOTH polarities, so the search reaches the optimum on either
    /// side of the [0, 180] phase split. Before the fix the search was locked to the baseline polarity and
    /// stalled at 25.8663 (sigma 5.1196) while a both-polarity reference reached 17.3286 (sigma 1.6583). The
    /// diagnostic below (same staged search, phase ceiling 350 deg) is why the lock, not the staging, was the
    /// cause: pol +1 at 275 deg and pol -1 at 95 deg are the same drive.
    /// </summary>
    [Fact]
    public void TheSearchReachesTheOptimumOnBothSidesOfThePolaritySplit()
    {
        DualSubMeasurement measurement = Fixture(F1A, F1B);
        OptimizerResult search = SubwooferOptimizer.Search(measurement, SearchOptions);
        OptimizerCandidate reference = SubwooferOptimizer.BruteForce(measurement, ReferenceOptions)
            ?? throw new InvalidOperationException("the reference found no legal candidate");

        Assert.NotNull(search.Best);
        Assert.True(search.Best!.Score <= reference.Score + 1e-9,
            $"PD-1: search {search.Best.Score:R} vs reference {reference.Score:R}");
        Assert.True(search.Best.Summary.MeanStdDevDb <= reference.Summary.MeanStdDevDb + 1e-9,
            $"PD-1: search sigma {search.Best.Summary.MeanStdDevDb:R} vs reference {reference.Summary.MeanStdDevDb:R}");
        // Measured: 17.3224 (sigma 1.6512) against the coarse reference 17.3286 (sigma 1.6583).
        Assert.Equal(17.3224, search.Best.Score, 3);
        Assert.Equal(1.6512, search.Best.Summary.MeanStdDevDb, 3);
        Assert.True(search.Best.Score < 18.0, "the fixed search must not stall near the old 25.87 locked result");

        // The reference reaches the same drive on the other side of the split; the spelling may differ.
        Assert.Equal(17.3286, reference.Score, 3);
        Assert.Equal(1.6583, reference.Summary.MeanStdDevDb, 3);

        // The wide-phase diagnostic spells the SAME drive the other way; it must score the same as the fixed
        // search rather than 8.54 worse.
        OptimizerResult wide = SubwooferOptimizer.Search(measurement, SearchOptions with { PhaseMaxDegrees = 350.0 });
        Assert.True(wide.Best!.Score <= reference.Score + 1e-3,
            $"wide-phase staged search reached {wide.Best.Score:F4} against the reference {reference.Score:F4}");
        Assert.True(Math.Abs(search.Best.Score - wide.Best.Score) < 1e-6,
            $"the two spellings of the same drive must score the same: {search.Best.Score:R} vs {wide.Best.Score:R}");
    }

    /// <summary>
    /// PD-2 (fixed): an exactly cancelling candidate still produces a NaN score (the trap assertion below),
    /// but a non-finite score is now mapped to +inf instead of being compared, so it can never become the
    /// incumbent and the search no longer walks by distance-to-measured-setting. Before the fix this fixture
    /// returned 38.7272 (sigma 7.7526); after it the search beats the (coarse) both-polarity reference.
    /// </summary>
    [Fact]
    public void AnExactlyCancellingCandidateNoLongerDerailsTheSearch()
    {
        double[][] a = [.. F2A.Select(row => (double[])row.Clone())];
        var rotation = Complex.FromPolarCoordinates(1.0, 20.0 * Math.PI / 180.0);
        var zeroed = -(rotation * new Complex(F2B[0][0], F2B[0][1]));
        a[0] = [zeroed.Real, zeroed.Imaginary];
        DualSubMeasurement measurement = Fixture(a, F2B);

        // The trap is real: this one candidate's total is exactly zero, so its score is NaN.
        var trap = SubwooferSetting.FromDegrees(0.0, 20.0, 1, 0.0);
        double trapScore = ObjectiveFunction.Terms(
            SpatialMetrics.Compute(SubwooferModel.Combine(measurement, trap)), 0.0).Score(ObjectiveWeights.Default);
        Assert.True(double.IsNaN(trapScore), "the injected candidate no longer cancels exactly, so PD-2 is not being tested");

        OptimizerResult search = SubwooferOptimizer.Search(measurement, SearchOptions);
        OptimizerCandidate reference = SubwooferOptimizer.BruteForce(measurement, ReferenceOptions)
            ?? throw new InvalidOperationException("the reference found no legal candidate");

        OptimizerStage coarse = search.Trace.Single(stage => stage.Stage.StartsWith("joint 2-D coarse", StringComparison.Ordinal));
        Assert.True(double.IsFinite(coarse.BestScore), "a non-finite score must never become the incumbent");
        Assert.True(double.IsFinite(search.Best!.Score));
        Assert.True(double.IsFinite(search.ScoreBefore) && double.IsFinite(search.ScoreAfter));

        Assert.True(search.Best.Score <= reference.Score + 1e-9,
            $"PD-2: search {search.Best.Score:R} vs reference {reference.Score:R}");
        // Measured: 13.7304 against the coarse reference 13.7360.
        Assert.Equal(13.7304, search.Best.Score, 3);
        Assert.True(search.Best.Score < 15.0, "the fixed search must not stall near the old 38.73 poisoned result");
    }

    /// <summary>Builds a 4-position, 3-bin (40/70/100 Hz) measurement from interleaved (re, im) tables.</summary>
    private static DualSubMeasurement Fixture(double[][] aValues, double[][] bValues)
    {
        var band = new FrequencyBand(5.0, 200.0);
        double[] frequencies = [40.0, 70.0, 100.0];
        var a = new List<PositionResponse>(4);
        var b = new List<PositionResponse>(4);
        for (int i = 0; i < 4; i++)
        {
            var aBins = new List<FrequencyResponse>(3);
            var bBins = new List<FrequencyResponse>(3);
            for (int k = 0; k < 3; k++)
            {
                int index = (i * 3) + k;
                aBins.Add(SubwooferModel.Bin(frequencies[k], new Complex(aValues[index][0], aValues[index][1])));
                bBins.Add(SubwooferModel.Bin(frequencies[k], new Complex(bValues[index][0], bValues[index][1])));
            }
            a.Add(new PositionResponse($"p{i}", band, aBins));
            b.Add(new PositionResponse($"p{i}", band, bBins));
        }
        return new DualSubMeasurement(a, b);
    }
}
