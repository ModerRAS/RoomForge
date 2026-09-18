namespace AudioOptimizer.Tests.Ui;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioOptimizer.Audio;
using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using AudioOptimizer.UI.Controls;
using AudioOptimizer.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// §24's twelve guided steps: presented over the existing flow, derived rather than stored, reachable from the
/// shell. The evidence shape is the standing one — text on the visual tree, geometry on the bitmap, every
/// assertion paired with a negative twin, no committed baseline.
/// </summary>
[Collection(RenderCollection.Name)]
public sealed class MeasurementWizardViewTests(ITestOutputHelper output)
{
    /// <summary>Mirrors WizardView.xaml's pinned geometry, restated here rather than read from the control.</summary>
    private const double RootMargin = 8.0;
    private const double GuideHeight = 22.0;
    private const double ListMargin = 6.0;
    private const double RowPitch = 26.0;

    /// <summary>Inside the first column of every row, so one x reaches all twelve headings.</summary>
    private const int HeadingProbeX = 30;

    private const int RenderWidth = 1000;
    private const int RenderHeight = 460;

    /// <summary>The brief's order — the content of this array is the assertion.</summary>
    private static readonly string[] BriefOrder =
    [
        "Input device", "Output device", "Sample rate", "Sweep settings", "Measurement area", "Generate points",
        "Sub A", "Sub B", "Sub A+B", "Analysis", "Optimize", "Results",
    ];

    [Fact]
    public void The_twelve_steps_are_presented_in_the_briefs_order_and_the_first_unfinished_one_is_current()
    {
        var flow = new MeasurementFlowViewModel(new FakeAudioBackend());
        var wizard = new MeasurementWizardViewModel(flow, new OptimizerPanelViewModel());

        IReadOnlyList<WizardStep> steps = wizard.Steps;
        Assert.Equal(12, steps.Count);
        Assert.Equal((IEnumerable<string>)BriefOrder, steps.Select(step => step.Title));
        Assert.Equal((IEnumerable<int>)Enumerable.Range(1, 12), steps.Select(step => step.Number));
        Assert.Equal(
            (IEnumerable<string>)[.. BriefOrder.Select((title, index) => $"{index + 1}. {title}")],
            steps.Select(step => step.Heading));

        // Derived, not stored: with no device list read yet, the guide is on step 1 …
        Assert.Single(steps, step => step.IsCurrent);
        Assert.Equal("Input device", steps.Single(step => step.IsCurrent).Title);
        Assert.Equal("next", steps.Single(step => step.IsCurrent).StateText);
        Assert.False(steps.Single(step => step.IsCurrent).IsDone);
        // … the negative twin: presenting twelve steps must not mark the later ones as reached.
        Assert.DoesNotContain(steps.Where(step => step.Number > 6), step => step.IsDone);
        Assert.Contains("Step 1 of 12", wizard.GuideText, StringComparison.Ordinal);

        string directory = NewDirectory("order");
        try
        {
            flow.ProjectDirectory = directory;
            flow.CountX = 1;
            flow.CountY = 1;
            flow.CountZ = 2;
            flow.RefreshDevices();
            flow.StartSession();
            Assert.NotNull(flow.Session);

            steps = wizard.Steps;
            // One slot per point per mode — the point count comes from the grid, never from a literal.
            Assert.Equal(3 * flow.Grid.PointCount, flow.Session.Slots.Count);
            Assert.Equal(6, flow.Session.Slots.Count);
            Assert.Contains($"= {flow.Grid.PointCount} points", steps[5].Detail, StringComparison.Ordinal);
            Assert.Equal(flow.GridText, steps[5].Detail);

            // Devices chosen and a session open: steps 1-6 are done and the guide moved on its own.
            Assert.Equal("Sub A", steps.Single(step => step.IsCurrent).Title);
            Assert.All(steps.Where(step => step.Number <= 6), step => Assert.True(step.IsDone));
            // A mode step reports that mode's own total out of the session, and nothing measured is not "done".
            Assert.Equal("0 of 2 measured", steps[6].Detail);
            Assert.Equal("0 of 2 measured", steps[7].Detail);
            Assert.Equal("0 of 2 measured", steps[8].Detail);
            Assert.Equal("0 of 6 measured", steps[9].Detail);
            Assert.All(steps.Where(step => step.Number is >= 7 and <= 12), step => Assert.False(step.IsDone));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Each_of_the_twelve_steps_renders_in_its_own_band_with_its_own_text()
    {
        var flow = new MeasurementFlowViewModel(new FakeAudioBackend());
        var wizard = new MeasurementWizardViewModel(flow, new OptimizerPanelViewModel());
        // A real device name in rows 1 and 2, so the rows carry derived text rather than a placeholder.
        flow.RefreshDevices();

        int realized = 0;
        RenderedImage image = RenderHarness.Render(
            () =>
            {
                var view = new WizardView { DataContext = wizard };
                view.Measure(new Size(RenderWidth, RenderHeight));
                view.Arrange(new Rect(0, 0, RenderWidth, RenderHeight));
                view.UpdateLayout();
                var list = (ItemsControl)view.FindName("StepList")!;
                realized = list.Items.Count;
                // The twelfth row must be REALIZED, not merely listed: a probe below an unrealized row would have
                // measured the absence of a container rather than the absence of a row.
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(11));
                return view;
            },
            width: RenderWidth,
            height: RenderHeight);

        Assert.Equal(12, realized);
        Assert.Equal(RenderWidth, image.Width);                 // 96 dpi, pinned size: the probes below depend on it
        Assert.Equal(RenderHeight, image.Height);
        Assert.True(image.InkPixels() > 500, $"the wizard rendered {image.InkPixels()} ink pixels");

        IReadOnlyList<WizardStep> steps = wizard.Steps;
        for (int index = 0; index < steps.Count; index++)
        {
            int y = RowProbeY(index);
            // Geometry on the bitmap: this row's own band carries ink where the pinned pitch puts it.
            Assert.False(image.IsBlankNear(HeadingProbeX, y, 14), $"row {index + 1} has no ink at y={y}");
            // Text on the tree: the row's heading, state and detail are all present, so a row drawn but not bound
            // (or bound to the wrong step) fails here rather than looking fine in the bitmap.
            Assert.Contains(image.Texts, text => text == steps[index].Heading);
            Assert.Contains(image.Texts, text => text == steps[index].StateText);
            Assert.Contains(image.Texts, text => text == steps[index].Detail);
        }

        // The guide line has ink of its own above the first row, and it names the first step that is not done —
        // here step 6: the two device rows and the three setting rows are already satisfied by the device list and
        // the defaults, and no session has been started yet.
        Assert.False(image.IsBlankNear(HeadingProbeX, 16, 6), "the guide line drew nothing");
        Assert.StartsWith("Step 6 of 12: Generate points", wizard.GuideText, StringComparison.Ordinal);
        Assert.Contains(image.Texts, text => text == wizard.GuideText);
        // … and the negative twins: nothing above the first row's band, nothing where a thirteenth row would be.
        Assert.True(image.IsBlankNear(HeadingProbeX, 3, 3), "the wizard drew ink above its first row's band");
        Assert.True(image.IsBlankNear(HeadingProbeX, RowProbeY(12), 8), "the wizard drew a thirteenth row");

        string png = image.WritePng("m6-wizard");
        Assert.True(File.Exists(png));
        Assert.Empty(Directory.GetFiles(TestPaths.RepoRoot, "*.png", SearchOption.AllDirectories));
        output.WriteLine($"{image.InkPixels()} ink px of {image.Width * image.Height}, rows at y="
            + string.Join(",", Enumerable.Range(0, 12).Select(RowProbeY)) + " :: " + string.Join(" | ", image.Texts));
    }

    [Fact]
    public void The_wizard_is_reachable_from_the_shell_and_absent_from_the_tree_until_its_tab_is_selected()
    {
        var backend = new FakeAudioBackend();
        bool tabFoundOnTheShell = false;
        RenderedImage unselected = RenderHarness.Render(
            () =>
            {
                var shell = new ShellView(backend);
                shell.Width = 1240;
                shell.Height = 700;
                shell.Measure(new Size(1240, 700));
                shell.Arrange(new Rect(0, 0, 1240, 700));
                shell.UpdateLayout();
                // Reachability asserted ON THE SHELL, from the strip's own items: a wizard that exists only as a
                // view model, or as a panel nobody can select, is the defect this catches.
                tabFoundOnTheShell = shell.TabStrip.Items.OfType<TabItem>().Any(item => Equals(item.Header, "Wizard"));
                tabFoundOnTheShell &= shell.Wizard.Steps.Count == 12;
                return shell;
            },
            width: 1240,
            height: 700);

        Assert.True(tabFoundOnTheShell, "the shell offers no Wizard tab");
        // The twin: before the tab is selected there is no wizard on screen at all — not a blank row, the whole list.
        Assert.DoesNotContain(unselected.Texts, text => text == "1. Input device");
        Assert.DoesNotContain(unselected.Texts, text => text.StartsWith("Step 1 of 12", StringComparison.Ordinal));

        RenderedImage selected = RenderHarness.Render(
            () =>
            {
                var shell = new ShellView(backend);
                shell.Width = 1240;
                shell.Height = 700;
                shell.Measure(new Size(1240, 700));
                shell.Arrange(new Rect(0, 0, 1240, 700));
                TabItem tab = shell.TabStrip.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Wizard"));
                tab.IsSelected = true;
                shell.UpdateLayout();
                return shell;
            },
            width: 1240,
            height: 700);

        Assert.Contains(selected.Texts, text => text == "1. Input device");
        Assert.Contains(selected.Texts, text => text == "12. Results");
        Assert.Contains(selected.Texts, text => text.StartsWith("Step 1 of 12", StringComparison.Ordinal));
        // The selected state is the shell's, not the tab's own guess: the strip reports it as the current item.
        Assert.True(selected.Texts.Count > unselected.Texts.Count);
        output.WriteLine($"unselected {unselected.Texts.Count} text runs, selected {selected.Texts.Count}");
    }

    [Fact]
    public async Task The_results_step_flips_from_upcoming_to_done_when_the_search_runs()
    {
        string directory = Seed("flip");
        try
        {
            MeasurementFlowViewModel flow = Flow(directory);
            var panel = new OptimizerPanelViewModel();
            var wizard = new MeasurementWizardViewModel(flow, panel);
            panel.Refresh(flow);
            Assert.True(panel.CanRun);

            IReadOnlyList<WizardStep> before = wizard.Steps;
            // A and B are measured, so the analysis has something to draw and the search has positions to pair …
            Assert.True(before.Single(step => step.Title == "Analysis").IsDone);
            Assert.True(before.Single(step => step.Title == "Optimize").IsDone);
            // … and the guide is still on the A+B pass nobody has measured, not on results nobody has produced.
            Assert.Equal("Sub A+B", before.Single(step => step.IsCurrent).Title);
            WizardStep results = before.Single(step => step.Title == "Results");
            Assert.False(results.IsDone);
            Assert.Equal("upcoming", results.StateText);
            Assert.Equal("no optimisation run yet", results.Detail);

            int notifications = 0;
            wizard.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MeasurementWizardViewModel.Steps)) notifications++;
            };
            Assert.Equal(0, notifications);      // the twin: nothing announced before anything happened

            await panel.RunAsync();

            Assert.NotNull(panel.Result);
            Assert.True(notifications > 0, "the wizard never re-read its steps when the optimiser produced a result");
            results = wizard.Steps.Single(step => step.Title == "Results");
            Assert.True(results.IsDone);
            Assert.Equal("done", results.StateText);
            Assert.Equal(panel.RecommendationText, results.Detail);
            Assert.NotEmpty(results.Detail);
            // The unmeasured pass still holds the guide: producing a result does not mark step 9 as measured.
            Assert.Equal("Sub A+B", wizard.Steps.Single(step => step.IsCurrent).Title);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void The_wizard_renders_byte_identically_twice_and_moves_when_the_flow_moves()
    {
        var flow = new MeasurementFlowViewModel(new FakeAudioBackend());
        var wizard = new MeasurementWizardViewModel(flow, new OptimizerPanelViewModel());
        flow.RefreshDevices();

        (RenderedImage first, RenderedImage second) = RenderHarness.RenderTwice(
            () => new WizardView { DataContext = wizard },
            width: RenderWidth,
            height: RenderHeight);
        Assert.Equal(first.Png, second.Png);        // same run, same pixels: no clock, no ordering, no randomness

        // The twin that gives that equality meaning: the same render after a state change is DIFFERENT, so
        // byte-identical is a property of the drawing and not of a blank or hardcoded image.
        string directory = NewDirectory("twice");
        try
        {
            flow.ProjectDirectory = directory;
            flow.CountX = 1;
            flow.CountY = 1;
            flow.CountZ = 2;
            flow.StartSession();
            Assert.NotNull(flow.Session);

            RenderedImage after = RenderHarness.Render(
                () => new WizardView { DataContext = wizard },
                width: RenderWidth,
                height: RenderHeight);
            Assert.NotEqual(first.Png, after.Png);
            // The difference is the derived text, not a repainted background: the mode rows now report the
            // session's own totals where they said there was no session.
            Assert.Contains(after.Texts, text => text == "0 of 2 measured");
            Assert.DoesNotContain(first.Texts, text => text == "0 of 2 measured");
            Assert.Contains(first.Texts, text => text == "no session yet");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void The_wizard_is_a_presenter_with_no_commands_and_no_writable_state_of_its_own()
    {
        // "A presenter over the existing flow" as a property of the type rather than of a comment: it has no command…
        Assert.DoesNotContain(
            typeof(MeasurementWizardViewModel).GetProperties(),
            property => typeof(ICommand).IsAssignableFrom(property.PropertyType));
        // … and no writable member, so step state cannot be pushed into it: the flow owns that, and a stored copy
        // is the second source of truth this design refuses.
        Assert.DoesNotContain(
            typeof(MeasurementWizardViewModel).GetProperties(),
            property => property.SetMethod is { IsPublic: true });
        // Discriminating twin: the same scan finds commands on the flow, so the assertion above can fail for the
        // right reason rather than because the scan is blind.
        Assert.Contains(
            typeof(MeasurementFlowViewModel).GetProperties(),
            property => typeof(ICommand).IsAssignableFrom(property.PropertyType));
    }

    private static int RowProbeY(int rowIndex)
        => (int)(RootMargin + GuideHeight + ListMargin + (rowIndex * RowPitch) + 8);

    private static string NewDirectory(string name)
        => Path.Combine(Path.GetTempPath(), $"roomforge-wizard-{name}-{Guid.NewGuid():N}");

    private static void Cleanup(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
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

    /// <summary>A and B measured at both heights — the states the optimiser panel documents pairing by point.</summary>
    private static string Seed(string name)
    {
        string directory = NewDirectory(name);
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
        foreach (MeasurementSlot slot in session.Slots.Where(slot => slot.Mode != SubMode.AB).ToList())
            Assert.Equal(MeasurementSlotState.Done, runner.Run(slot).Slot.State);

        return directory;
    }
}
