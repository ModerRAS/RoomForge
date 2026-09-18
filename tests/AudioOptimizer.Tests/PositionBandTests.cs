namespace AudioOptimizer.Tests;

using System.IO;
using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using AudioOptimizer.Optimization;
using Xunit;

/// <summary>
/// The band guard at the type level: a full-spectrum bin list cannot enter the engine, because the position that would
/// carry it cannot be constructed. That is stronger evidence than a source scan — the wrong accessor cannot be laundered
/// in at all — and it makes the row count free: bins cannot exceed the band, so a filter that zeroed out-of-band bins
/// instead of removing them is unrepresentable.
/// </summary>
public sealed class PositionBandTests
{
    [Fact]
    public void A_position_cannot_be_constructed_outside_its_band()
    {
        var band = new FrequencyBand(20.0, 150.0);
        FrequencyResponse[] full =
            [OptimizationTestData.Bin(0.0, 1.0, 0.0), OptimizationTestData.Bin(25.0, 1.0, 0.0), OptimizationTestData.Bin(24000.0, 1.0, 0.0)];

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => new PositionResponse("p0", band, full));
        Assert.Contains("outside the analysis band", error.Message, StringComparison.Ordinal);
        Assert.Equal(0.0, (double)error.ActualValue!);                 // the first offending bin, the DC one

        // In-band bins are accepted, including exactly at both edges (the band is inclusive), and the band travels with
        // the position so every consumer can state what the numbers cover.
        var ok = new PositionResponse("p0", band, [OptimizationTestData.Bin(20.0, 1.0, 0.0), OptimizationTestData.Bin(150.0, 1.0, 0.0)]);
        Assert.Equal(band, ok.AnalysisBand);
        Assert.Equal(2, ok.Bins.Count);
        Assert.True(ok.Bins.All(bin => ok.AnalysisBand.Contains(bin.FrequencyHz)));
    }

    [Fact]
    public void Every_measured_position_carries_exactly_the_in_band_rows()
    {
        // End to end: the session's band-limited bins are constructible and the raw spectrum is not, and the row count
        // equals the in-band count. 143999 samples of IR → NextPowerOfTwo 262144 → Δf 0.18310546875 → 710 in-band bins.
        string directory = Path.Combine(Path.GetTempPath(), $"roomforge-posband-{Guid.NewGuid():N}");
        var sweep = new SweepSettings(20.0, 150.0, 1.0, 48000);
        MeasurementSession session = MeasurementSession.Start(directory, MeasurementGrid.Create(1.8, 1.0, 0.6, 1, 1, 2), sweep);
        MeasurementSlot slot = session.MarkDone(session.NextPending!, new double[96000], new double[143999], peakMagnitude: 0.1);

        FrequencyResponse[] inBand = session.InBandResponseOf(slot)!;
        var position = new PositionResponse(slot.Point.Id, session.Band, inBand);
        Assert.Equal(710, position.Bins.Count);
        Assert.Equal(inBand.Length, position.Bins.Count);
        Assert.Equal(131073, session.FrequencyResponseOf(slot)!.Length);

        // The summary states the band its numbers cover, read from the shared reference position the grid guard has
        // already proved representative — that is what lets a panel report configured-and-achieved, not just the request.
        SpatialSummary summary = SpatialMetrics.Compute([position]);
        Assert.Equal(position.AnalysisBand, summary.AnalysisBand);
        Assert.Equal(session.Band, summary.AnalysisBand);        // the spectrum is still there to be refused
        // This is not a band test: it feeds the RAW whole-spectrum accessor output plus the session's band to the
        // constructor and asserts it throws. That is the type-level proof the wrong accessor cannot be laundered into the
        // engine — stronger than a source scan, which checks call sites, because this checks the value is unconstructible.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PositionResponse(slot.Point.Id, session.Band, session.FrequencyResponseOf(slot)!));
    }

    [Fact]
    public void A_and_B_that_disagree_about_their_band_are_refused()
    {
        // Validate() is the entry contract on every engine path, and it must check the band as well as the grid: the
        // model's `new PositionResponse(a.PointId, a.AnalysisBand, …)` clone gives the combined position A's band whatever B's was, so a mismatch would
        // be laundered into a result that passes validation while B's actual band differed.
        var a = new PositionResponse("p0", new FrequencyBand(20.0, 150.0), [OptimizationTestData.Bin(50.0, 1.0, 0.0)]);
        var b = new PositionResponse("p0", new FrequencyBand(20.0, 100.0), [OptimizationTestData.Bin(50.0, 1.0, 0.0)]);
        var measurement = new DualSubMeasurement([a], [b]);
        ArgumentException byBand = Assert.Throws<ArgumentException>(() => measurement.Validate());
        Assert.Contains("covers", byBand.Message, StringComparison.Ordinal);
        Assert.Contains("in A", byBand.Message, StringComparison.Ordinal);

        // The same rule via the one shared predicate, and the grid rule still fires first for a frequency difference.
        Assert.True(PositionGrid.SameGrid(a, a));
        Assert.Equal(-1, PositionGrid.FirstDifferentBin(a, a));
        var shifted = new PositionResponse("p0", new FrequencyBand(20.0, 150.0), [OptimizationTestData.Bin(60.0, 1.0, 0.0)]);
        Assert.Equal(0, PositionGrid.FirstDifferentBin(a, shifted));
        Assert.Throws<ArgumentException>(() => new DualSubMeasurement([a], [shifted]).Validate());
    }

    [Fact]
    public void Positions_that_disagree_about_their_grid_or_band_are_refused()
    {
        // Built by construction, not by path: equal counts with different grids, both inside the band. No real session
        // can produce a mixed grid — one session has one sample rate and one IR length, so one Δf, and the in-band count
        // is strictly increasing in the FFT size — but a synthesised producer can, and the level at index k is labelled
        // from totals[0]'s k-th frequency, so a mismatch would be mislabelled rather than merely mixed.
        var band = new FrequencyBand(20.0, 150.0);
        var first = new PositionResponse("p0", band, [OptimizationTestData.Bin(20.5, 1.0, 0.0), OptimizationTestData.Bin(21.0, 1.0, 0.0)]);
        var second = new PositionResponse("p1", band, [OptimizationTestData.Bin(20.6, 1.0, 0.0), OptimizationTestData.Bin(21.1, 1.0, 0.0)]);
        Assert.Equal(first.Bins.Count, second.Bins.Count);
        ArgumentException byGrid = Assert.Throws<ArgumentException>(() => SpatialMetrics.Compute([first, second]));
        Assert.Contains("Hz but", byGrid.Message, StringComparison.Ordinal);

        var narrower = new PositionResponse("p2", new FrequencyBand(20.0, 100.0), [OptimizationTestData.Bin(20.5, 1.0, 0.0), OptimizationTestData.Bin(21.0, 1.0, 0.0)]);
        ArgumentException byBand = Assert.Throws<ArgumentException>(() => SpatialMetrics.Compute([first, narrower]));
        Assert.Contains("covers", byBand.Message, StringComparison.Ordinal);

        // Guard order: the empty-list contract is unchanged (SpatialMetricsTests asserts the same), and a degenerate
        // one-bin band is legal — no span validation.
        Assert.Throws<ArgumentException>(() => SpatialMetrics.Compute([]));
        var oneBinBand = new FrequencyBand(50.0, 50.0);
        var oneBin = new PositionResponse("p0", oneBinBand, [OptimizationTestData.Bin(50.0, 1.0, 0.0)]);
        Assert.Equal(0.0, oneBin.AnalysisBand.SpanHz, 12);      // degenerate bands are legal: no span check
        Assert.Single(SpatialMetrics.Compute([oneBin]).PerFrequency);
    }
}
