namespace AudioOptimizer.Tests.Ui;

using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows.Controls;
using AudioOptimizer.UI.Controls;
using AudioOptimizer.Visualization;
using Xunit.Abstractions;

/// <summary>
/// M0 — the capture-evidence milestone. The phase does not pass M0 without a working, asserted render path, so
/// these tests are the gate: pinned dimensions, a stated ink measure with a blank control as its negative twin,
/// same-run determinism, data-derived pixel probes computed independently of the production transform, and
/// artifacts confined to the system temp directory.
/// </summary>
public sealed class RenderEvidenceTests(ITestOutputHelper output)
{
    private const int PinnedWidth = 320;
    private const int PinnedHeight = 200;

    /// <summary>The series the probe plots, chosen so no vertex lands on the frame: range −12…0 dB, data −9…−1.</summary>
    private static readonly double[] Series = [-9, -3, -7, -1, -6];

    private const double DataMin = -12.0;
    private const double DataMax = 0.0;

    private static RenderProbe NewProbe()
    {
        var probe = new RenderProbe();
        probe.Plot(Series, DataMin, DataMax);
        return probe;
    }

    [Fact]
    public void The_render_path_produces_a_pinned_non_blank_png_in_temp()
    {
        RenderedImage image = RenderHarness.Render(NewProbe, PinnedWidth, PinnedHeight);

        // 320 × 200 CSS px at 96 DPI = 320 × 200 device px: PixelWidth = round(320 · 96/96) = 320.
        Assert.Equal(PinnedWidth, image.Width);
        Assert.Equal(PinnedHeight, image.Height);
        Assert.Equal(96, image.Dpi);

        // Ink measure: frame (304 + 164) px of stroke, one 5-vertex polyline, and two antialiased text runs.
        // State a floor well under the measured value so the assertion fails on a blank/missing render, not on
        // font drift. The blank twin below proves the measure discriminates at all.
        int ink = image.InkPixels();
        Assert.InRange(ink, 300, PinnedWidth * PinnedHeight);
        output.WriteLine($"M0 ink: {ink} px non-white of {PinnedWidth * PinnedHeight} ({ink * 100.0 / (PinnedWidth * PinnedHeight):F2} %)");

        string path = image.WritePng("m0-probe");
        Assert.StartsWith(Path.GetTempPath(), path);
        Assert.True(File.Exists(path));
        output.WriteLine($"M0 png: {path} ({image.Png.Length} bytes)");

        // PNG signature 89 50 4E 47 — the encoder produced a real PNG, not an empty buffer.
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], image.Png[..4]);

        // The artifact-hygiene rule: nothing rendered may land in the repository tree.
        string[] pngsInRepo = [.. Directory.GetFiles(TestPaths.RepoRoot, "*.png", SearchOption.AllDirectories)];
        Assert.Empty(pngsInRepo);
    }

    [Fact]
    public void Two_renders_in_the_same_run_are_byte_identical()
    {
        (RenderedImage first, RenderedImage second) = RenderHarness.RenderTwice(NewProbe, PinnedWidth, PinnedHeight);

        Assert.Equal(first.Png, second.Png);                       // encoded bytes identical
        Assert.Equal(first.Bgra, second.Bgra);                     // and pixel buffers identical
        output.WriteLine($"M0 determinism: png {first.Png.Length} bytes identical, "
            + $"first pixel {first.PixelAt(0, 0)}, last pixel {first.PixelAt(PinnedWidth - 1, PinnedHeight - 1)}");
    }

    [Fact]
    public void A_blank_surface_reports_zero_ink()
    {
        // Negative twin of the ink measure: if this reported ink, every ink assertion above would be vacuous.
        RenderedImage blank = RenderHarness.Render(
            () => new Canvas { Width = PinnedWidth, Height = PinnedHeight, Background = System.Windows.Media.Brushes.White },
            PinnedWidth,
            PinnedHeight);

        Assert.Equal(0, blank.InkPixels());
        output.WriteLine($"blank-twin ink: {blank.InkPixels()} px");
    }

    [Fact]
    public void The_series_is_at_the_independently_derived_data_coordinate()
    {
        RenderedImage image = RenderHarness.Render(NewProbe, PinnedWidth, PinnedHeight);

        // Asserted plot bounds (the control's own coordinates, restated here as literals so this test is not
        // reading the production transform): left 8, top 8, right 312, bottom 172.
        const double left = 8, top = 8, right = 312, bottom = 172;

        // The maximum of the series is index 3 (value −1). Derived with plain arithmetic, NOT via AxisScale:
        //   x = left + 3/4 · (right − left)              = 8 + 0.75 · 304        = 236
        //   y = bottom − ((−1) − (−12))/(0 − (−12)) · (bottom − top) = 172 − (11/12)·164 = 21.667
        const int expectedX = 236;
        double expectedY = 172 - 11.0 / 12.0 * 164;
        Assert.Equal(236.0, 8 + 3.0 / 4.0 * 304);
        Assert.Equal(21.666666666666668, expectedY, 1e-12);

        Assert.True(image.HasColourNear(expectedX, (int)Math.Round(expectedY), 1, RenderProbe.CurveColour),
            $"no curve colour within 1 px of ({expectedX}, {expectedY:F3})");

        // Absent twin: the same column well below the curve is background, so a mapping that painted the whole
        // plot area (or a transform that pinned every point to one corner) would fail here.
        Assert.True(image.IsBlankNear(expectedX, 120, 2), $"the plot is not blank at ({expectedX}, 120)");

        // The axes are asserted separately, at their own coordinates: 1 px aliased frame stroke, ±1 px.
        RgbColour frame = new(0x80, 0x80, 0x80);
        Assert.True(image.HasColourNear((int)left, 100, 1, frame), "no left frame edge at x = 8");
        Assert.True(image.HasColourNear((int)right, 100, 1, frame), "no right frame edge at x = 312");
        Assert.True(image.HasColourNear(160, (int)top, 1, frame), "no top frame edge at y = 8");
        Assert.True(image.HasColourNear(160, (int)bottom, 1, frame), "no bottom frame edge at y = 172");
        output.WriteLine($"probe: curve at ({expectedX}, {expectedY:F3}) present, blank at ({expectedX}, 120), "
            + "frame edges at x = 8/312 and y = 8/172");
    }

    [Fact]
    public void The_interpolation_label_is_absent_by_default_and_present_when_asked()
    {
        const string label = "Interpolated visualization";

        // §23 disclosure colour, restated as a literal: #B00020.
        RgbColour labelColour = new(0xB0, 0x00, 0x20);

        RenderedImage plain = RenderHarness.Render(NewProbe, PinnedWidth, PinnedHeight);
        Assert.DoesNotContain(label, plain.Texts);                 // NOT interpolating ⇒ no visible label
        Assert.Contains(plain.Texts, text => text.Contains("render probe", StringComparison.Ordinal));
        Assert.Equal(0, plain.CountColour(labelColour));           // and nothing of that colour was painted

        RenderedImage labelled = RenderHarness.Render(
            () =>
            {
                RenderProbe probe = NewProbe();
                probe.ShowInterpolatedLabel = true;
                return probe;
            },
            PinnedWidth,
            PinnedHeight);

        Assert.Contains(label, labelled.Texts);                    // present when it IS interpolating
        Assert.True(labelled.CountColour(labelColour) > 0);        // and it reached the pixels, not only the tree
        Assert.True(labelled.InkPixels() > plain.InkPixels());
        output.WriteLine($"label: visible text {labelled.Texts.Count(t => t == label)}, "
            + $"label-colour pixels {plain.CountColour(labelColour)} -> {labelled.CountColour(labelColour)}, "
            + $"ink {plain.InkPixels()} -> {labelled.InkPixels()}");
    }

    [Fact]
    public void Visualization_has_no_wpf_reference_and_the_ui_is_where_wpf_lives()
    {
        Assembly visualization = typeof(PlotArea).Assembly;
        string[] references = [.. visualization.GetReferencedAssemblies().Select(name => name.Name!)];

        // Vacuity guard first: the negative checks below are worthless if the list is empty.
        Assert.Contains("System.Runtime", references);
        Assert.DoesNotContain("PresentationCore", references);
        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("WindowsBase", references);

        // The target framework is the plain one, not the -windows TFM the UI project uses: no WPF or Windows
        // API is even reachable from the visualization layer.
        Assert.Equal(".NETCoreApp,Version=v10.0", visualization.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);

        // Contrast: WPF really is referenced somewhere, so the negative checks are not passing because WPF is
        // missing from the build. The UI assembly carries it.
        Assembly ui = typeof(RenderProbe).Assembly;
        string[] uiReferences = [.. ui.GetReferencedAssemblies().Select(name => name.Name!)];
        Assert.Contains("PresentationFramework", uiReferences);
        Assert.Contains("PresentationCore", uiReferences);
        Assert.Contains("WindowsBase", uiReferences);
        // NOTE (measured): TargetFrameworkAttribute reports ".NETCoreApp,Version=v10.0" for net10.0-WINDOWS as
        // well — the attribute alone cannot tell the two TFMs apart. So the TFM check is a necessary condition
        // (this is not net48/netstandard) and the discriminating evidence is the reference set: WPF assemblies
        // in the UI, none in Visualization. Both are asserted.
        string visualizationFramework = visualization.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
        string uiFramework = ui.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
        Assert.Equal(".NETCoreApp,Version=v10.0", visualizationFramework);
        Assert.Equal(visualizationFramework, uiFramework);
        Assert.Contains("PresentationFramework", uiReferences);

        // The discriminator the TFM attribute fails to be: an OS-specific TFM emits TargetPlatformAttribute and
        // SupportedOSPlatformAttribute into the generated AssemblyInfo, a plain net10.0 TFM emits neither. This is
        // a runtime assertion on the loaded assembly, so a stale or mistargeted build fails it — unlike a
        // statement of intent. It complements the reference-set proof, which stays primary because it is causal.
        Assert.Null(visualization.GetCustomAttribute<TargetPlatformAttribute>());
        Assert.Empty(visualization.GetCustomAttributes<SupportedOSPlatformAttribute>());
        Assert.NotNull(ui.GetCustomAttribute<TargetPlatformAttribute>());
        output.WriteLine($"platform attribute: visualization {(visualization.GetCustomAttribute<TargetPlatformAttribute>() is null ? "absent" : "present")}, "
            + $"ui {(ui.GetCustomAttribute<TargetPlatformAttribute>() is null ? "absent" : "present")}");
        output.WriteLine($"visualization refs: {string.Join(", ", references)}");
        output.WriteLine($"ui refs contain WPF: {uiReferences.Count(r => r.StartsWith("Presentation") || r == "WindowsBase")} assemblies");
    }
}
