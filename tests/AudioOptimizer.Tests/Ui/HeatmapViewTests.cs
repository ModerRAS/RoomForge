namespace AudioOptimizer.Tests.Ui;

using System.IO;
using AudioOptimizer.Audio;
using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using AudioOptimizer.UI.Controls;
using AudioOptimizer.UI.ViewModels;
using AudioOptimizer.Visualization;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The heatmap panel's render evidence: the colour bar's legend and the ramp move together when the source reference
/// changes, every plane is drawn against one shared scale, the §23 statement appears only when the figure is
/// interpolated, and the frames are built from the grid rather than from a constant.
/// </summary>
[Collection(RenderCollection.Name)]
public sealed class HeatmapViewTests(ITestOutputHelper output)
{
    [Fact]
    public void The_legend_and_the_ramp_move_together_when_the_reference_changes()
    {
        // Switching the SOURCE, not the string: if the ramp had silently kept a different context, a legend-text
        // assertion alone would still pass. So both artefacts are asserted to move — the wording to each reference's
        // own AxisLabel, and the fraction a fixed +3 dB step covers: 3/24 = 0.125 under the ±12 dB band-mean
        // reference, 3/30 = 0.1 under the −30…0 max-normalised one.
        MeasurementFlowViewModel flow = Flow(Seed("legend"));
        RenderedImage bandMean = RenderHarness.Render(() => View(new HeatmapViewModel(LevelReference.RelativeToBandMean), flow), 420.0, 300.0);
        RenderedImage normalised = RenderHarness.Render(() => View(new HeatmapViewModel(LevelReference.NormalisedToMax), flow), 420.0, 300.0);

        Assert.Contains(bandMean.Texts, text => text.Contains(LevelReference.RelativeToBandMean.AxisLabel, StringComparison.Ordinal));
        Assert.Contains(normalised.Texts, text => text.Contains(LevelReference.NormalisedToMax.AxisLabel, StringComparison.Ordinal));
        Assert.DoesNotContain(bandMean.Texts, text => text.Contains("SPL", StringComparison.Ordinal));
        Assert.DoesNotContain(normalised.Texts, text => text.Contains("SPL", StringComparison.Ordinal));

        // A 3 dB step taken inside BOTH spans (−10 → −7 dB): 3/24 = 0.125 of the ±12 dB band-mean ramp and
        // 3/30 = 0.1 of the −30…0 max-normalised ramp. (A step from 0 to +3 dB would sit outside the normalised
        // range and be clamped, which measures the clamp rather than the mapping.)
        Assert.Equal(0.125, RampDelta(LevelReference.RelativeToBandMean, flow), 12);
        Assert.Equal(0.1, RampDelta(LevelReference.NormalisedToMax, flow), 12);
        output.WriteLine(string.Join(" | ", bandMean.Texts));
    }

    [Fact]
    public void Every_plane_is_drawn_against_one_shared_scale()
    {
        MeasurementFlowViewModel flow = Flow(Seed("planes"));
        var model = new HeatmapViewModel();
        model.Refresh(flow);

        // One plane per analysed height — from grid.CountZ, which is 2 here, not from a literal.
        Assert.Equal(2, model.PlaneCount);
        // Comparable panels: identical endpoints and caption, so "brighter" can only mean "louder".
        Assert.Equal(model.Planes[0].Scale.Label, model.Planes[1].Scale.Label);
        Assert.Equal(model.Planes[0].Scale.MinDb, model.Planes[1].Scale.MinDb, 12);
        Assert.Equal(model.Planes[0].Scale.MaxDb, model.Planes[1].Scale.MaxDb, 12);

        // The frames come from the measured grid, and the frequency used is a real bin: reported with the request.
        RenderedImage image = RenderHarness.Render(() => View(model, flow), 420.0, 300.0);
        Assert.Contains(image.Texts, text => text.Contains("height Z", StringComparison.Ordinal));
        // The requested/achieved pair is the panel's own text (the plane view shows only its figure), and both
        // numbers are present so the request can never be read as the measurement.
        Assert.Contains("requested", model.FrequencyText, StringComparison.Ordinal);
        Assert.Contains("analysis bin", model.FrequencyText, StringComparison.Ordinal);
        Assert.DoesNotContain(image.Texts, text => text.Contains("Interpolated", StringComparison.Ordinal));
        Assert.NotNull(model.PositionMap);                              // §22.3's frequency × position figure
    }

    [Fact]
    public void The_interpolated_label_appears_on_the_heatmap_only_when_it_is_interpolated()
    {
        MeasurementFlowViewModel flow = Flow(Seed("interp"));
        var exact = new HeatmapViewModel();
        exact.Refresh(flow);
        var smooth = new HeatmapViewModel { Interpolated = true };
        smooth.Refresh(flow);
        Assert.True(smooth.Planes[0].Interpolated);
        Assert.False(exact.Planes[0].Interpolated);

        RenderedImage exactImage = RenderHarness.Render(() => View(exact, flow), 420.0, 300.0);
        RenderedImage smoothImage = RenderHarness.Render(() => View(smooth, flow), 420.0, 300.0);
        Assert.DoesNotContain(exactImage.Texts, text => text.Contains(CurveChart.InterpolatedLabel, StringComparison.Ordinal));
        Assert.Contains(smoothImage.Texts, text => text.Contains(CurveChart.InterpolatedLabel, StringComparison.Ordinal));
    }

    [Fact]
    public void The_frequency_by_position_map_is_probed_on_its_own_axes_and_pinned_to_real_bins()
    {
        // §22.3 is frequency × position, so the probe uses THIS figure's own geometry: MapBounds = (20, 24, 320, 224)
        // and cell (col,row) spans 320/Columns wide and 224/Rows tall.
        MeasurementFlowViewModel flow = Flow(Seed("positionmap"));
        var model = new HeatmapViewModel();
        model.Refresh(flow);
        Heatmap map = Assert.IsType<Heatmap>(model.PositionMap);
        Assert.Equal("frequency (Hz)", map.XAxisLabel);
        Assert.Equal("position (measured point)", map.YAxisLabel);

        // The columns are the analysis's own IN-BAND bins, echoed from the session — never a band-edge literal and never
        // the whole spectrum. Measured for this seed: the FR calculator returns a one-sided spectrum of 131073 bins,
        // DC → 24000 Hz, so Δf = 24000/131072 = **0.18310546875 Hz** (not 0.3662 — that was the hardware log's
        // two-sided figure, and assuming it is how I produced a wrong 355). In [20, 150]: first index ceil(20/Δf) = 110
        // → 20.1416015625 Hz, last index floor(150/Δf) = 819 → 149.96337890625 Hz, so exactly 710 in-band bins.
        MeasurementSession session = Assert.IsType<MeasurementSession>(flow.Session);
        MeasurementSlot done = session.Slots.First(slot => slot.State == MeasurementSlotState.Done);
        FrequencyResponse[] full = session.FrequencyResponseOf(done)!;
        double[] bins = [.. session.InBandResponseOf(done)!.Select(bin => bin.FrequencyHz)];
        Assert.Equal(131073, full.Length);                            // the whole spectrum the calculator returns …
        Assert.Equal(0.0, full[0].FrequencyHz);
        Assert.Equal(24000.0, full[^1].FrequencyHz, 6);
        Assert.Equal(710, bins.Length);                               // … and the band that was actually excited
        Assert.Equal(20.1416015625, bins[0], 9);
        Assert.Equal(149.96337890625, bins[^1], 9);
        Assert.DoesNotContain(0.0, bins);                             // the DC bin is not in the band
        Assert.DoesNotContain(24000.0, bins);                         // nor is Nyquist
        Assert.Equal(bins.Length, map.Columns);                       // one column per in-band bin
        Assert.Equal(2, map.Rows);                                    // two measured points in the 1×1×2 grid

        int rectangles = 0;
        RenderedImage image = RenderHarness.Render(
            () =>
            {
                var view = new HeatmapView();
                view.Show(map);
                // Counted on the view's own canvas rather than through the visual tree: the visual tree is built by a
                // layout pass, and a count taken before it reads zero — which would have looked like "no cells drawn".
                var canvas = (System.Windows.Controls.Canvas)view.Content;
                rectangles = canvas.Children.OfType<System.Windows.Shapes.Rectangle>().Count();
                return view;
            },
            420.0,
            300.0);

        // Geometry: one rectangle per cell, plus the frame, plus the 24 colour-bar slices — from the map's own shape.
        Assert.Equal((map.Columns * map.Rows) + 1 + 24, rectangles);
        // This figure's OWN axes, and the discriminating twin: re-asserting the plane layout would prove nothing here,
        // because a plane is position × position (width × depth) and this figure is frequency × position.
        Assert.Equal("frequency (Hz)", map.XAxisLabel);
        Assert.Equal("position (measured point)", map.YAxisLabel);
        Assert.NotEqual(model.Planes[0].XAxisLabel, map.XAxisLabel);
        Assert.NotEqual(model.Planes[0].YAxisLabel, map.YAxisLabel);
        Assert.Equal("width (m)", model.Planes[0].XAxisLabel);
        Assert.Contains(image.Texts, text => text.Contains("frequency (Hz)", StringComparison.Ordinal));
        Assert.Contains(image.Texts, text => text.Contains("position (measured point)", StringComparison.Ordinal));
        // The twin: the figure names its axis and never prints a band edge it did not measure.
        Assert.DoesNotContain(image.Texts, text => text.Contains("20 Hz", StringComparison.Ordinal));
        Assert.DoesNotContain(image.Texts, text => text.Contains("150 Hz", StringComparison.Ordinal));
        // No colour probe on this figure at a real band: 320 px over 710 in-band columns is 320/710 = 0.4507 px per
        // column, below one pixel, so a probed "cell" would measure the rasteriser's rounding rather than the data. The
        // cell geometry is asserted above instead, and the colour probe is made on a narrow synthetic band in its own
        // fact, where cells exceed a pixel.
        Assert.True(320.0 / map.Columns < 1.0, $"columns are sub-pixel at {map.Columns} bins");
        output.WriteLine($"map {map.Columns}×{map.Rows}, full spectrum {full.Length} bins 0–{full[^1].FrequencyHz:F0} Hz, "
            + $"in-band {bins.Length} bins {bins[0]:F4}–{bins[^1]:F4} Hz, {rectangles} rectangles, "
            + $"{320.0 / map.Columns:F4} px per column");
    }

    [Fact]
    public void The_frequency_selector_offers_only_the_measured_band_and_reports_the_bin_it_used()
    {
        MeasurementFlowViewModel flow = Flow(Seed("band"));
        var model = new HeatmapViewModel();
        model.Refresh(flow);
        MeasurementSession session = Assert.IsType<MeasurementSession>(flow.Session);
        double[] bins = [.. session.InBandResponseOf(session.Slots.First(slot => slot.State == MeasurementSlotState.Done))!
            .Select(bin => bin.FrequencyHz)];

        // Every offer is inside the band the sweep excited, and the count is a value rather than a phrase: a
        // phrase-level assertion ("contains 'analysis bin'") passes for any count, which is how a selector offering
        // 0…24000 Hz in 5 Hz steps — 4801 requests, 99.1 % of them outside the band — survived review.
        Assert.All(model.Frequencies, frequency => Assert.InRange(frequency, session.Sweep.StartHz, session.Sweep.EndHz));
        Assert.Equal(25, model.Frequencies.Count);                    // measured: 25 offers — ceil(20.1416/5)·5 = 25 to
        Assert.Equal(25.0, model.Frequencies[0], 12);                 // floor(149.9634/5)·5 = 145, in steps of 5
        Assert.Equal(145.0, model.Frequencies[^1], 12);
        Assert.DoesNotContain(0.0, model.Frequencies);                // twin: the DC bin is not offered
        Assert.DoesNotContain(24000.0, model.Frequencies);            // twin: Nyquist is not offered
        Assert.DoesNotContain(5.0, model.Frequencies);                // twin: a low out-of-band request is not offered
        Assert.DoesNotContain(5000.0, model.Frequencies);             // twin: a high out-of-band request is not offered

        // The report is the bin actually used, and the count in it is the IN-BAND count — measured, not illustrative.
        Assert.Equal(710, bins.Length);
        Assert.Contains("nearest of 710 bins", model.FrequencyText, StringComparison.Ordinal);
        Assert.Contains("analysis bin 25.0854 Hz", model.FrequencyText, StringComparison.Ordinal);

        // A request outside the band cannot reach an out-of-band bin: the ladder replaces it with a legal request, and
        // the achieved bin stays inside the band either way. A direct request clamps into the in-band list.
        model.RequestedHz = 5000.0;
        model.Refresh(flow);
        Assert.InRange(model.RequestedHz, session.Sweep.StartHz, session.Sweep.EndHz);
        (int _, double clamped) = BinSnap.Snap(bins, 5000.0);
        Assert.Equal(149.96337890625, clamped, 9);
        Assert.Contains(clamped, bins);
        (int __, double lowClamp) = BinSnap.Snap(bins, 0.0);
        Assert.Equal(20.1416015625, lowClamp, 9);

        // The inclusive band and the band-limited list together pin this: a request of exactly 150.0 has no bin to land
        // on (the last in-band bin is 149.9634) and clamps to it, and 20.0 clamps up to 20.1416 — the design working,
        // stated here so it cannot later be "fixed" into an exclusive band.
        (int ___, double edgeHigh) = BinSnap.Snap(bins, 150.0);
        Assert.Equal(149.96337890625, edgeHigh, 9);
        (int ____, double edgeLow) = BinSnap.Snap(bins, 20.0);
        Assert.Equal(20.1416015625, edgeLow, 9);

        // Requested versus achieved, the third instance in this milestone: the label names the configured band AND the
        // extent actually analysed, so it cannot be read as claiming bins at the edges.
        Assert.Contains("requested band 20–150 Hz", model.Message, StringComparison.Ordinal);
        Assert.Contains("710 bins 20.1416–149.9634 Hz", model.Message, StringComparison.Ordinal);
        output.WriteLine($"offers {model.Frequencies.Count} ({model.Frequencies[0]}…{model.Frequencies[^1]} Hz), "
            + $"text '{model.FrequencyText}', clamp(5000) → {clamped:F4} Hz, clamp(0) → {lowClamp:F4} Hz");
    }

    [Fact]
    public void The_position_maps_colours_are_probed_on_a_narrow_band_where_cells_exceed_a_pixel()
    {
        // A real band gives 0.90 px per column, so the colour probe needs a narrow band. Synthetic and deliberately
        // small (4 columns × 3 rows) with a known 6 dB spread, so the geometry is exact: MapBounds = (20, 24, 320, 224)
        // restated as literals, cell = 80 × 74.67 px, and the probe coordinates are plain arithmetic on those numbers.
        var rows = new List<IReadOnlyList<double>>
        {
            new double[] { 0.0, 0.0, 0.0, 0.0 },
            new double[] { 0.0, 3.0, 3.0, 0.0 },
            new double[] { 0.0, 0.0, 0.0, 6.0 },
        };
        Heatmap map = Heatmaps.Matrix(
            "Level by frequency and position",
            "frequency (Hz)",
            "position (measured point)",
            rows,
            LevelReference.RelativeToBandMean,
            Heatmaps.SharedContext(rows.SelectMany(row => row)));
        Assert.Equal(4, map.Columns);
        Assert.Equal(3, map.Rows);

        RenderedImage image = RenderHarness.Render(
            () =>
            {
                var view = new HeatmapView();
                view.Show(map);
                return view;
            },
            420.0,
            300.0);

        // Cell (col,row) centre = (20 + (col+0.5)·80, 24 + (row+0.5)·74.667) → (60, 61), (300, 210).
        RgbColour topLeft = map.At(0, 0).Colour;
        RgbColour bottomRight = map.At(3, 2).Colour;
        Assert.True(image.HasColourNear(60, 61, 3, topLeft, 32), "cell (0,0) centre");
        Assert.True(image.HasColourNear(300, 210, 3, bottomRight, 32), "cell (3,2) centre");
        Assert.NotEqual(topLeft, bottomRight);                        // a 6 dB spread, so the two cells must differ
        // Negative twin: cell (0,0)'s colour is absent in the left margin, outside the plot bounds.
        Assert.False(image.HasColourNear(5, 61, 2, topLeft, 32));
        output.WriteLine($"4×3 band: (60,61)={topLeft.ToHex()} (300,210)={bottomRight.ToHex()}");
    }

    private static HeatmapView View(HeatmapViewModel model, MeasurementFlowViewModel flow)
    {
        model.Refresh(flow);
        var view = new HeatmapView();
        view.Show(model.Planes[0]);
        return view;
    }

    /// <summary>The ramp fraction a fixed +3 dB step covers under this reference, read from the built scale itself.</summary>
    private static double RampDelta(LevelReference reference, MeasurementFlowViewModel flow)
    {
        var model = new HeatmapViewModel(reference);
        model.Refresh(flow);
        ColourScale scale = model.Scale!;
        return scale.FractionAt(-7.0) - scale.FractionAt(-10.0);
    }

    private static MeasurementFlowViewModel Flow(string directory)
    {
        var flow = new MeasurementFlowViewModel(new FakeAudioBackend())
        {
            ProjectDirectory = directory,
            CountX = 1,
            CountY = 1,
            CountZ = 2,
        };
        flow.RefreshDevices();
        flow.StartSession();
        Assert.NotNull(flow.Session);
        return flow;
    }

    /// <summary>Two measured planes at the grid's two heights, written through the measurement engine.</summary>
    private static string Seed(string name)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"roomforge-m4-{name}-{Guid.NewGuid():N}");
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
        foreach (MeasurementSlot slot in session.Slots.Where(slot => slot.Mode == SubMode.A).ToList())
        {
            MeasurementRunOutcome outcome = runner.Run(slot);
            Assert.Equal(MeasurementSlotState.Done, outcome.Slot.State);
        }

        return directory;
    }
}
