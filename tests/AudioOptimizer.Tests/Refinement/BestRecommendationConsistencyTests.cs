namespace AudioOptimizer.Tests.Refinement;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;

/// <summary>
/// The result's <c>Best</c> must describe the best LEGAL candidate the search actually found. The report-grid step
/// re-measures snapped candidates, so it can find a legal setting better than the round-capped incumbent: on the
/// mirror pair through the real chain, <c>Best</c> was 28.842894873 while <c>Recommended</c>/<c>ScoreAfter</c> was
/// 28.800787442 — "Best" was worse than the recommendation it framed. This pins the relationship rather than a
/// constant, so the fixture stays valid as the search improves.
/// </summary>
public class BestRecommendationConsistencyTests
{
    [Fact]
    public void Best_is_never_worse_than_the_final_recommendation()
    {
        var lab = new VirtualLab(
            SimulationConfig.Default,
            [
                new VirtualSubwoofer(new Position(0.35, 0.35, 0.30)),
                new VirtualSubwoofer(new Position(2.95, 3.25, 0.30), 0.8, -1, 0.0, 0.0),
            ],
            [.. ListeningRegion.Default.Points.Where(point => point.GridZ == 0)]);
        DualSubMeasurement input = lab.AsOptimizerInput(lab.MeasureAll());

        OptimizerResult result = SubwooferOptimizer.Search(input, new OptimizerOptions { IncludeDelay = true, MaxBoostLimitDb = 30.0 });

        Assert.NotNull(result.Best);
        Assert.NotNull(result.Recommended);
        Assert.True(result.Best!.Score <= result.ScoreAfter + 1e-9,
            $"Best {result.Best.Score:R} must not be worse than the final recommendation {result.ScoreAfter:R}");
    }
}
