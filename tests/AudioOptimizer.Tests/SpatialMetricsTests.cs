using AudioOptimizer.Optimization;

namespace AudioOptimizer.Tests;

/// <summary>
/// The executable definition of the spatial statistics. Every assertion carries the arithmetic, because these
/// are the headline numbers the product reports.
/// </summary>
public class SpatialMetricsTests
{
    [Fact]
    public void StandardDeviationIsThePopulationOne()
    {
        // levels 0, 0, 0, 2 dB → mean 0.5; Σ(x−mean)² = 3·0.25 + 2.25 = 3.0; /N = 0.75 → σ = √0.75 = 0.8660254037844386.
        // The sample deviation (÷(N−1)) would be √1 = 1.0 — the N grid positions are the whole population.
        FrequencyMetrics metrics = SpatialMetrics.AtFrequency([0.0, 0.0, 0.0, 2.0], 50.0);
        Assert.Equal(0.5, metrics.MeanDb, 12);
        Assert.Equal(Math.Sqrt(0.75), metrics.StdDevDb, 12);
        Assert.Equal(0.8660254037844386, metrics.StdDevDb, 12);
        Assert.NotEqual(1.0, metrics.StdDevDb);
    }

    [Fact]
    public void MedianAveragesTheTwoCentralValuesWhenNIsEven()
    {
        // even N: (2 + 4)/2 = 3.0 — deliberately not nearest-rank P50, which would pick one and drop the other.
        Assert.Equal(3.0, SpatialMetrics.Median([4.0, 2.0]), 12);
        // odd N: the middle order statistic.
        Assert.Equal(2.0, SpatialMetrics.Median([3.0, 1.0, 2.0]), 12);
    }

    [Fact]
    public void PercentilesAreNearestRankWithoutInterpolation()
    {
        // N = 5, ascending −6, −2, 0, 3, 9: P10 = ceil(0.10·5) − 1 = index 0 → −6.0;
        // P90 = ceil(0.90·5) − 1 = index 4 → 9.0; P90 − P10 = 15.0.
        double[] levels = [-6.0, -2.0, 0.0, 3.0, 9.0];
        Assert.Equal(-6.0, SpatialMetrics.PercentileNearestRank(levels, 10.0), 12);
        Assert.Equal(9.0, SpatialMetrics.PercentileNearestRank(levels, 90.0), 12);
        Assert.Equal(15.0, SpatialMetrics.AtFrequency(levels, 50.0).P90P10Db, 12);

        // N = 4, ascending 0, 0, 0, 2: P10 = ceil(0.4) − 1 = index 0 → 0.0; P90 = ceil(3.6) − 1 = index 3 → 2.0.
        FrequencyMetrics four = SpatialMetrics.AtFrequency([0.0, 0.0, 0.0, 2.0], 50.0);
        Assert.Equal(0.0, four.P10Db, 12);
        Assert.Equal(2.0, four.P90Db, 12);
        Assert.Equal(2.0, four.P90P10Db, 12);
    }

    [Fact]
    public void BandAggregatesAreTheMeansAndMaximaOfThePerFrequencyRows()
    {
        // Two positions (magnitudes 1 and 2) at two frequencies:
        //   50 Hz: levels 0 and 6.020599913279624 dB → mean 3.010299956639812, σ 3.010299956639812, range 6.020599913279624
        //   100 Hz: levels −6.020599913279624 and 0 dB → mean −3.010299956639812, σ 3.010299956639812, range 6.020599913279624
        // so mean σ = 3.010299956639812, mean range = mean P90−P10 = 6.020599913279624,
        // peak above the frequency mean = 3.010299956639812, worst null below it = 3.010299956639812.
        var totals = new List<PositionResponse>
        {
            new("p0", OptimizationTestData.Band, [OptimizationTestData.Bin(50.0, 1.0, 0.0), OptimizationTestData.Bin(100.0, 0.5, 0.0)]),
            new("p1", OptimizationTestData.Band, [OptimizationTestData.Bin(50.0, 2.0, 0.0), OptimizationTestData.Bin(100.0, 1.0, 0.0)]),
        };

        SpatialSummary summary = SpatialMetrics.Compute(totals);
        Assert.Equal(3.010299956639812, summary.MeanStdDevDb, 12);
        Assert.Equal(6.020599913279624, summary.MeanRangeDb, 12);       // (6.020599913279624 + 6.020599913279624) / 2
        Assert.Equal(6.020599913279624, summary.MeanP90P10Db, 12);
        Assert.Equal(3.010299956639812, summary.MaxPeakAboveMeanDb, 12);
        Assert.Equal(3.010299956639812, summary.WorstNullDb, 12);
        Assert.Equal(2, summary.PerFrequency.Count);
    }

    [Fact]
    public void SpreadIsComputedInDbNotLinearMagnitude()
    {
        // The same two positions, magnitude 1 and 2 (0 dB and +6.020599913279624 dB):
        //   dB domain    → σ = 3.010299956639812 dB (half the dB spread)
        //   linear domain→ mean 1.5, population σ = 0.5
        // The spec's headline numbers are dB, so σ must be the former.
        FrequencyMetrics metrics = SpatialMetrics.AtFrequency([0.0, 6.020599913279624], 50.0);
        Assert.Equal(3.010299956639812, metrics.StdDevDb, 12);
        Assert.NotEqual(0.5, metrics.StdDevDb);
    }

    [Fact]
    public void SpreadIsInvariantToAPerFrequencyLevelShift()
    {
        // Shifting every position at a frequency by the same dB changes the level, never the uniformity.
        FrequencyMetrics quiet = SpatialMetrics.AtFrequency([-20.0, -18.0, -25.0], 50.0);
        FrequencyMetrics loud = SpatialMetrics.AtFrequency([10.0, 12.0, 5.0], 50.0);
        Assert.Equal(quiet.StdDevDb, loud.StdDevDb, 12);
        Assert.Equal(quiet.RangeDb, loud.RangeDb, 12);
        Assert.Equal(quiet.P90P10Db, loud.P90P10Db, 12);
        Assert.Equal(quiet.MaxDb - quiet.MeanDb, loud.MaxDb - loud.MeanDb, 12);
        Assert.Equal(quiet.MeanDb - quiet.MinDb, loud.MeanDb - loud.MinDb, 12);
    }

    [Fact]
    public void NothingAssumesTwentySevenPositions()
    {
        // Five positions — count is whatever the data has.
        var totals = new List<PositionResponse>();
        for (int i = 0; i < 5; i++) totals.Add(new PositionResponse($"p{i}", OptimizationTestData.Band, [OptimizationTestData.Bin(50.0, 1.0 + i, 0.0)]));

        SpatialSummary summary = SpatialMetrics.Compute(totals);
        Assert.Single(summary.PerFrequency);
        // levels 20·log10([1,2,3,4,5]) = 0, 6.0206, 9.5424, 12.0412, 13.9794 → mean 8.316725... = Σ/5
        double expectedMean = (0.0 + 6.020599913279624 + 9.542425094393248 + 12.041199826559248 + 13.979400086720377) / 5.0;
        Assert.Equal(expectedMean, summary.PerFrequency[0].MeanDb, 12);
        Assert.Equal(0.0, summary.PerFrequency[0].P10Db, 12);
        Assert.Equal(13.979400086720377, summary.PerFrequency[0].P90Db, 12);
    }

    [Fact]
    public void RaggedInputIsRejectedRatherThanAveraged()
    {
        var ragged = new List<PositionResponse>
        {
            new("p0", OptimizationTestData.Band, [OptimizationTestData.Bin(50.0, 1.0, 0.0)]),
            new("p1", OptimizationTestData.Band, [OptimizationTestData.Bin(50.0, 1.0, 0.0), OptimizationTestData.Bin(100.0, 1.0, 0.0)]),
        };
        Assert.Throws<ArgumentException>(() => SpatialMetrics.Compute(ragged));
        Assert.Throws<ArgumentException>(() => SpatialMetrics.Compute([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpatialMetrics.PercentileNearestRank([1.0], 0.0));
    }
}
