namespace AudioOptimizer.Tests.Ui;

using System.Windows;
using System.Windows.Controls;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.Measurement;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;
using AudioOptimizer.UI.Controls;
using AudioOptimizer.UI.ViewModels;
using AudioOptimizer.Visualization;
using Xunit.Abstractions;

/// <summary>
/// The Offline Simulation page: reachable from the shell, and — at the level of the objects it hands the controls —
/// producing one figure per configuration, one 3 × 3 plane per measured height and the band's nine numbers. The
/// figures are <c>ResponseCurves</c>' and <c>Heatmaps</c>' own, which is the point of asserting on their outputs rather
/// than on pixels here; the render harness covers reachability and layout.
/// </summary>
/// <summary>It renders through the harness, so it runs in the serialised render collection like every other view test.</summary>
[Collection(RenderCollection.Name)]
public class SimulationPanelViewTests(ITestOutputHelper output)
{
    private static readonly FrequencyBand Band = FrequencyBand.Of(SimulationConfig.Default.Sweep);

    /// <summary>One measurement at one grid point, with a level that varies across the grid so the planes differ.</summary>
    private static SimulatedMeasurement Fake(SubMode mode, MeasurementPoint point, double levelDb)
    {
        FrequencyResponse[] bins = [.. new[] { 25.0, 50.0, 75.0, 100.0, 125.0 }
            .Select(frequency => new FrequencyResponse(frequency, Math.Pow(10.0, levelDb / 20.0), 0.0, levelDb, 0.0, 0.0))];
        var result = new PointMeasurementResult(
            Recording: [],
            ImpulseResponse: [],
            Response: bins,
            Alignment: new SweepArrivalAnalysis(-1, 0, 0, 0, new ImpulseResponse([1.0], 48000)),
            Issues: [],
            PeakMagnitude: 1.0,
            FftSize: 1024,
            SweepSampleCount: 48000);
        return new SimulatedMeasurement(mode, point, result);
    }

    private static SimulationOutcome FakeOutcome(SubMode[] modes, bool withOptimizer = false)
    {
        IReadOnlyList<MeasurementPoint> points = ListeningRegion.Default.Points;
        var measurements = new List<SimulatedMeasurement>();
        foreach (SubMode mode in modes)
            foreach (MeasurementPoint point in points)
                measurements.Add(Fake(mode, point, 6.0 - (point.GridX * 1.5) - (point.GridY * 1.0) - point.GridZ));

        var scenario = SimulationScenarios.TwoSubSimple;
        var positions = measurements.Where(measurement => measurement.Mode == modes[0])
            .Select(measurement => new PositionResponse(measurement.Point.Id, Band, MeasurementSession.InBand(measurement.Result.Response, Band)))
            .ToList();

        return new SimulationOutcome(
            scenario,
            measurements,
            SpatialMetrics.Compute(positions),
            withOptimizer ? SubwooferOptimizer.Search(new DualSubMeasurement(positions, positions, positions), new OptimizerOptions { MaxBoostLimitDb = 3.0 }) : null,
            null,
            []);
    }

    [Fact]
    public void The_page_is_reachable_from_the_shell_and_states_what_it_does()
    {
        var backend = new FakeAudioBackend();
        bool tabFound = false;
        RenderedImage image = RenderHarness.Render(
            () =>
            {
                var shell = new ShellView(backend);
                shell.Width = 1240;
                shell.Height = 700;
                shell.Measure(new Size(1240, 700));
                shell.Arrange(new Rect(0, 0, 1240, 700));
                shell.UpdateLayout();
                tabFound = shell.TabStrip.Items.OfType<TabItem>().Any(item => Equals(item.Header, "Offline Simulation"));
                TabItem tab = shell.TabStrip.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Offline Simulation"));
                tab.IsSelected = true;
                shell.UpdateLayout();
                return shell;
            },
            width: 1240,
            height: 700);

        Assert.True(tabFound, "the shell offers no Offline Simulation tab");
        Assert.Contains(image.Texts, text => text == "Offline simulation");
        // The status line is the selected scenario's own description: the page states which experiment is loaded.
        Assert.Contains(image.Texts, text => text.StartsWith("single-sub:", StringComparison.Ordinal));
        Assert.Contains(image.Texts, text => text == "Generate simulation");
        // The scenario selector is populated from the scenarios themselves, not from a hand-written list.
        Assert.Contains(image.Texts, text => text.StartsWith("S1 single sub", StringComparison.Ordinal));
        output.WriteLine($"{image.InkPixels()} ink px, texts: {string.Join(" | ", image.Texts.Take(12))}");
    }

    [Fact]
    public void Selecting_a_scenario_loads_its_own_numbers_and_editing_them_changes_only_the_experiment()
    {
        var panel = new SimulationPanelViewModel { Scenario = SimulationScenarios.DeepNull };

        Assert.Equal(SimulationScenarios.DeepNull.Config.Room.LengthMetres, panel.RoomLength, 12);
        Assert.Equal(SimulationScenarios.DeepNull.Subs[1].Position.X, panel.SubBX, 12);
        Assert.Equal(3.0, panel.Configured().Optimize!.MaxBoostLimitDb);

        // Editing a position edits the experiment; the scenario's declared answer is untouched.
        panel.SubBX = 1.25;
        panel.RoomLength = 4.0;
        panel.NoiseLevel = 0.0;
        SimulationScenario configured = panel.Configured();

        Assert.Equal(4.0, configured.Config.Room.LengthMetres, 12);
        Assert.Equal(1.25, configured.Subs[1].Position.X, 12);
        Assert.Equal(0.0, configured.Config.MicrophoneNoiseLevel);
        Assert.Equal(SimulationScenarios.DeepNull.Subs[1].PhaseDegrees, configured.Subs[1].PhaseDegrees);
        Assert.Equal(SimulationScenarios.DeepNull.Subs[1].Polarity, configured.Subs[1].Polarity);
        Assert.Equal(SimulationScenarios.DeepNull.GroundTruthSetting, configured.GroundTruthSetting);

        // The listening grid follows the room: 27 microphones inside it, whatever the extents.
        Assert.Equal(27, configured.Microphones.Count);
        Assert.All(configured.Microphones, point => Assert.InRange(point.X, 0.0, 4.0));

        // A sub outside its own room is refused by name rather than simulated: the page surfaces that through the same
        // status line every other failure goes through.
        panel.SubBX = 9.0;
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => panel.Configured());
        Assert.Contains("outside the", refused.Message, StringComparison.Ordinal);
        panel.SubBX = 1.25;
    }

    [Fact]
    public void The_page_builds_one_curve_per_configuration_and_one_3x3_plane_per_measured_height()
    {
        SimulationOutcome outcome = FakeOutcome([SubMode.A, SubMode.B, SubMode.AB]);

        IReadOnlyList<CurvePlot> curves = SimulationPanelViewModel.Curves(outcome);
        Assert.Equal(3, curves.Count);
        foreach ((CurvePlot plot, string mode) in curves.Zip(new[] { "A", "B", "AB" }))
        {
            Assert.Contains($"— {mode},", plot.Title, StringComparison.Ordinal);
            Assert.Equal(27, plot.Of(CurveRole.Position).Count);                    // every measured position has its own curve
            Assert.Single(plot.Of(CurveRole.Mean));
            Assert.Single(plot.Of(CurveRole.Band));                                 // the ±1σ ribbon
            Assert.Equal("dB (relative to band mean)", plot.YAxisLabel);
        }

        IReadOnlyList<Heatmap> planes = SimulationPanelViewModel.PlanesAt(outcome, 50.0);
        Assert.Equal(3, planes.Count);                                            // one per Z level, from the grid itself
        Assert.All(planes, plane =>
        {
            Assert.Equal(3, plane.Columns);
            Assert.Equal(3, plane.Rows);
            Assert.Equal(9, plane.Cells.Count);
            Assert.False(plane.Interpolated);                                     // 3 × 3 is the measured grid, not a guess
        });
        // One shared colour scale, so a brighter cell always means a louder level (the grids' own z term makes the
        // planes differ, which is what the shared context has to preserve).
        Assert.Single(planes.Select(plane => plane.Scale).Distinct());
        output.WriteLine(string.Join(" | ", planes.Select(plane => plane.Title)));
    }

    [Fact]
    public void The_band_s_spatial_numbers_are_all_nine_of_them_and_the_search_s_own_terms_beside_them()
    {
        SimulationOutcome outcome = FakeOutcome([SubMode.A, SubMode.B, SubMode.AB], withOptimizer: true);

        string stats = SimulationPanelViewModel.Describe(BandSpatialStats.Of(outcome.Measured));
        output.WriteLine(stats);
        foreach (string label in new[] { "mean", "median", "σ", "min", "max", "range", "P10", "P90", "P90−P10" })
            Assert.Contains(label, stats, StringComparison.Ordinal);
        Assert.Contains("dB", stats, StringComparison.Ordinal);

        string optimizer = SimulationPanelViewModel.DescribeOptimizer(outcome.Optimization);
        output.WriteLine(optimizer);
        Assert.Contains(outcome.Optimization!.Verdict.ToString(), optimizer, StringComparison.Ordinal);
        Assert.Contains("boost", optimizer, StringComparison.Ordinal);
        Assert.Contains("candidates rejected", optimizer, StringComparison.Ordinal);

        // No search and no declared correction are normal states, and both say so rather than showing an empty line.
        Assert.Equal("No search has run.", SimulationPanelViewModel.DescribeOptimizer(null));
        Assert.Contains("declares no known correction", SimulationPanelViewModel.DescribeGroundTruth(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generating_a_small_scenario_fills_the_page_from_the_shipped_chain()
    {
        var panel = new SimulationPanelViewModel { Scenario = SimulationScenarios.SingleSub };
        await panel.GenerateAsync();

        output.WriteLine(panel.Status);
        output.WriteLine(panel.PipelineText);
        output.WriteLine(panel.StatsText);

        Assert.False(panel.IsBusy);
        Assert.True(panel.CanGenerate);
        Assert.Contains("1 points measured", panel.PipelineText, StringComparison.Ordinal);
        Assert.Contains("A: 1/1 clean", panel.PipelineText, StringComparison.Ordinal);
        Assert.Single(panel.Plots);                                        // sub A only: there is no B and no A+B pass
        Assert.Equal("dB (relative to band mean)", panel.Plots[0].YAxisLabel);
        Assert.Empty(panel.Planes);                                        // one microphone is not a 3 × 3 plane
        Assert.Contains("P90−P10", panel.StatsText, StringComparison.Ordinal);
        Assert.Equal("No search has run.", panel.OptimizerText);
    }
}
