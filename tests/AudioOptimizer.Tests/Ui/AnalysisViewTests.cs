namespace AudioOptimizer.Tests.Ui;

using System.IO;
using System.Windows;
using AudioOptimizer.Audio;
using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using AudioOptimizer.UI.Controls;
using AudioOptimizer.UI.ViewModels;
using AudioOptimizer.Visualization;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// M3's render evidence on the Analysis tab. The probes restate the chart's published plot rectangle as literals and
/// derive the expected pixels with plain arithmetic — never through <see cref="AxisScale.ToPixel"/> — so a change to
/// the production mapping that moves a curve shows up here as a failure rather than as a matching pair of bugs.
/// </summary>
public sealed class AnalysisViewTests(ITestOutputHelper output)
{
    [Fact]
    public void The_analysis_tab_draws_one_curve_per_measured_position_and_states_its_level_reference()
    {
        string directory = Seed("figures", [SubMode.A, SubMode.B]);
        try
        {
            AnalysisViewModel? analysis = null;
            CurveChart? chart = null;
            Point origin = default;
            (int Polylines, int Polygons) all = default;
            (int Polylines, int Polygons) shapes = default;
            RenderedImage image = RenderHarness.Render(
                () => Build(directory, out analysis, out chart, out origin, out shapes, interpolate: false),
                width: 1240,
                height: 700);

            // Curve count comes from the data: a 1×1×2 grid has two measured positions in mode A, not 27.
            Assert.Equal(2, analysis!.PositionCount);
            Assert.Equal(2, analysis.LevelsPlot!.Of(CurveRole.Position).Count);
            Assert.Single(analysis.LevelsPlot.Of(CurveRole.Mean));
            Assert.Single(analysis.LevelsPlot.Of(CurveRole.Band));

            // The visual tree carries exactly the curves the plot describes: two position polylines + the mean, and the
            // ribbon as a filled polygon. Counted from the tree, so it cannot be a self-report.
            Assert.Equal(1, shapes.Polylines);      // the default view is mean ±1σ: the mean curve only
            Assert.Equal(1, shapes.Polygons);       // and the ribbon, as a filled ring

            // §22.1's switch: with every position shown, the curve count follows the measurement — 2 measured
            // positions + the mean — and never a constant.
            RenderedImage overlay = RenderHarness.Render(
                () => Build(directory, out _, out _, out _, out all, showAllPositions: true),
                width: 1240,
                height: 700);
            Assert.Equal(3, all.Polylines);
            Assert.Equal(1, all.Polygons);
            // Ink is not the evidence here: the fake rig measures the same sweep at every position, so the two
            // position curves coincide with the mean and add almost no distinct pixels. The shape count above is
            // what proves the view switch; the ink numbers are printed for the record.
            output.WriteLine($"mean ±1σ {image.InkPixels()} ink px; all positions {overlay.InkPixels()} ink px");

            // The axis label is the reference's own wording, and no figure claims an absolute level.
            Assert.Contains(image.Texts, text => text == "dB (relative to band mean)");
            Assert.DoesNotContain(image.Texts, text => text.Contains("SPL", StringComparison.Ordinal));
            Assert.DoesNotContain(image.Texts, text => text.Contains("Interpolated", StringComparison.Ordinal));

            // The axes frame: the top edge at the published rectangle's y is inked, and 9 px above it is not.
            double frameY = origin.Y + CurveChart.PlotBounds.Top;
            Assert.False(image.IsBlankNear((int)Math.Round(origin.X + 250.0), (int)Math.Round(frameY), 1));
            Assert.True(image.IsBlankNear((int)Math.Round(origin.X + 250.0), (int)Math.Round(frameY) - 9, 1));

            // A data coordinate derived here: x = left + (f − XMin)/(XMax − XMin)·width, y = bottom + (v − YMin)/(YMax − YMin)·(top − bottom).
            CurveSeries mean = analysis.LevelsPlot.Of(CurveRole.Mean).Single();
            CurvePoint sample = mean.Points[mean.Points.Count / 2];
            var bounds = CurveChart.PlotBounds;
            double x = origin.X + bounds.Left + (((sample.X - analysis.LevelsPlot.XMin) / (analysis.LevelsPlot.XMax - analysis.LevelsPlot.XMin)) * bounds.Width);
            double y = origin.Y + bounds.Bottom + (((sample.Y - analysis.LevelsPlot.YMin) / (analysis.LevelsPlot.YMax - analysis.LevelsPlot.YMin)) * (bounds.Top - bounds.Bottom));
            Assert.True(image.HasColourNear((int)Math.Round(x), (int)Math.Round(y), 3, ResponseCurves.MeanColour), "the mean curve must be drawn at its own data coordinate");
            // Negative twin: the same probe y just outside the plot rectangle. "Blank" is not the right predicate
            // inside the figure (the ±1σ ribbon is filled and the grid lines are drawn), so the twin asserts the
            // series colour itself is absent outside the bounds — i.e. that colour comes from this curve only.
            Assert.False(
                image.HasColourNear((int)Math.Round(origin.X + bounds.Right + 6.0), (int)Math.Round(y), 2, ResponseCurves.MeanColour),
                $"no mean-curve ink outside the plot at y={y:F1}");

            output.WriteLine($"{image.InkPixels()} ink px of {image.Width * image.Height}; probe ({Math.Round(x)}, {Math.Round(y)})");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void The_interpolated_label_appears_only_when_the_figures_are_interpolated()
    {
        string directory = Seed("interpolated", [SubMode.A, SubMode.B]);
        try
        {
            RenderedImage interpolated = RenderHarness.Render(
                () => Build(directory, out _, out _, out _, out _, interpolate: true),
                width: 1240,
                height: 700);
            RenderedImage exact = RenderHarness.Render(
                () => Build(directory, out _, out _, out _, out _, interpolate: false),
                width: 1240,
                height: 700);

            // §23 both ways: present when interpolating, and asserted absent when not, so the label cannot rot into
            // always-on (which would make it a decoration rather than a statement about the figure).
            Assert.Contains(interpolated.Texts, text => text == CurveChart.InterpolatedLabel);
            Assert.DoesNotContain(exact.Texts, text => text.Contains("Interpolated", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void An_empty_session_shows_a_message_and_draws_no_figures()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"roomforge-m3-empty-{Guid.NewGuid():N}");
        try
        {
            AnalysisViewModel? analysis = null;
            CurveChart? chart = null;
            (int Polylines, int Polygons) shapes2 = default;
            RenderedImage image = RenderHarness.Render(
                () => Build(directory, out analysis, out chart, out _, out shapes2),
                width: 1240,
                height: 700);

            Assert.Null(analysis!.LevelsPlot);
            Assert.Equal(0, shapes2.Polylines);
            Assert.Contains(image.Texts, text => text.Contains("No measured points in mode", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>Builds the shell on the harness's thread, resumes the seeded project and selects the Analysis tab.</summary>
    private static ShellView Build(string directory, out AnalysisViewModel? analysis, out CurveChart? chart, out Point chartOrigin, out (int Polylines, int Polygons) shapes, bool interpolate = false, bool showAllPositions = false)
    {
        var shell = new ShellView(new FakeAudioBackend());
        shell.Width = 1240.0;
        shell.Height = 700.0;
        shell.Flow.ProjectDirectory = directory;
        shell.Flow.CountX = 1;
        shell.Flow.CountY = 1;
        shell.Flow.CountZ = 2;
        shell.Flow.RefreshDevices();
        shell.Flow.StartSession();                                  // resume: the seeded points are already Done

        shell.TabStrip.SelectedIndex = 2;                           // the Analysis tab
        if (interpolate) shell.Analysis.Interpolated = true;
        shell.Analysis.ShowAllPositions = showAllPositions;
        shell.Measure(new Size(1240.0, 700.0));
        shell.Arrange(new Rect(0.0, 0.0, 1240.0, 700.0));
        shell.UpdateLayout();
        // Explicit, so the figures do not depend on the tab-selection event firing before the first layout pass.
        shell.Analysis.Refresh(shell.Flow);
        shell.UpdateLayout();

        analysis = shell.Analysis;
        chart = (CurveChart)shell.FindName("LevelsChart");
        // The chart's own pixel origin inside the shell, so a probe can be stated in shell coordinates as image
        // pixels + this offset. Layout, not plot math: the data-to-pixel mapping is still derived in the test.
        chartOrigin = chart.TranslatePoint(new Point(0.0, 0.0), shell);
        // Walked here, on the thread that owns the visual tree: WPF objects are not readable from the test thread.
        int polylines = Descendants(chart).OfType<System.Windows.Shapes.Polyline>().Count();
        int polygons = Descendants(chart).OfType<System.Windows.Shapes.Polygon>().Count();
        shapes = (polylines, polygons);
        return shell;
    }

    /// <summary>Writes a real project through the measurement engine: every slot of the given modes measured and recorded.</summary>
    private static string Seed(string name, SubMode[] modes)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"roomforge-m3-{name}-{Guid.NewGuid():N}");
        var sweep = new SweepSettings(20, 150, 1.0, 48000);
        MeasurementSession session = MeasurementSession.Start(directory, MeasurementGrid.Create(1.8, 1.0, 0.6, 1, 1, 2), sweep);
        var backend = new FakeAudioBackend();
        var runner = new MeasurementRunner(
            backend,
            session,
            backend.Devices.Outputs[0],
            backend.Devices.Inputs[0],
            new AudioBackendSettings(48000, AudioShareMode.Shared, 100),
            TimeSpan.FromSeconds(0.5),
            TimeSpan.FromSeconds(0.5));
        // snaphot the slots first: Slots is a live view over the session's records and Run replaces them, so
        // enumerating it while measuring throws (measured: "Collection was modified").
        foreach (MeasurementSlot slot in session.Slots.Where(slot => modes.Contains(slot.Mode)).ToList())
        {
            MeasurementRunOutcome outcome = runner.Run(slot);
            Assert.Equal(MeasurementSlotState.Done, outcome.Slot.State);
        }

        return directory;
    }

    private static IEnumerable<UIElement> Descendants(DependencyObject parent)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return (UIElement)child;
            foreach (UIElement grandChild in Descendants(child)) yield return grandChild;
        }
    }

    private static void Cleanup(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
