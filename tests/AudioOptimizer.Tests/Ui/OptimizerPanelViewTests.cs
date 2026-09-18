namespace AudioOptimizer.Tests.Ui;

using System.Globalization;
using System.IO;
using System.Numerics;
using AudioOptimizer.Audio;
using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using AudioOptimizer.Optimization;
using AudioOptimizer.UI.Controls;
using AudioOptimizer.UI.ViewModels;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The optimizer panel's evidence: the max-boost domain is a closed set at both ends (what the control offers, and what
/// the search can be given), the requested limit and the achieved boost are shown as two numbers, and §21's real
/// pipeline — measured A+B against the model's prediction — reaches the panel with the engine's own structured causes.
/// One real search is shared by the facts that need a result, because the search dominates the suite's runtime.
/// </summary>
public sealed class OptimizerPanelViewTests(ITestOutputHelper output)
{
    [Fact]
    public void The_offered_max_boost_set_is_exactly_three_values_and_no_other_reaches_the_search()
    {
        // SET equality, not "no negatives": the legal domain is {0, 3, 6} dB and nothing else.
        Assert.Equal([0.0, 3.0, 6.0], OptimizerPanelViewModel.AllowedMaxBoostDb.Order().ToArray());

        var panel = new OptimizerPanelViewModel();
        // Every offered value passes through unchanged: the offered set IS the set the search can see.
        foreach (double offered in OptimizerPanelViewModel.AllowedMaxBoostDb)
        {
            panel.MaxBoostDb = offered;
            Assert.Equal(offered, panel.MaxBoostDb, 12);
            Assert.Equal(offered, panel.BuildOptions().MaxBoostLimitDb, 12);
        }

        // A value outside the set cannot reach the optimizer call, and is refused rather than silently clamped — a
        // clamp would turn a −1 dB request into a 0 dB run nobody asked for.
        Assert.Equal(3, OptimizerPanelViewModel.AllowedMaxBoostDb.Count);
        foreach (double request in new[] { -1.0, 1.5, 4.0, 12.0, 0.5 })
        {
            panel.MaxBoostDb = request;
            Assert.Equal(6.0, panel.MaxBoostDb, 12);                                   // last legal value, unchanged
            double passed = panel.BuildOptions().MaxBoostLimitDb;
            Assert.Contains(passed, OptimizerPanelViewModel.AllowedMaxBoostDb);          // and the call stays in the set
            Assert.Equal(6.0, passed, 12);
        }

        Assert.Contains("refused", panel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_panel_shows_the_requested_limit_beside_the_achieved_boost_and_runs_off_the_caller_thread()
    {
        MeasurementFlowViewModel flow = Flow(Seed("boost"));
        var panel = new OptimizerPanelViewModel { MaxBoostDb = 3.0 };
        panel.Refresh(flow);
        Assert.True(panel.CanRun);

        // The one real search in this file: shared by every assertion that needs a result.
        int caller = Environment.CurrentManagedThreadId;
        var threads = new HashSet<int>();
        panel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(OptimizerPanelViewModel.IsBusy)) threads.Add(Environment.CurrentManagedThreadId);
        };
        await panel.RunAsync();

        OptimizerResult result = Assert.IsType<OptimizerResult>(panel.Result);
        Assert.Equal(3.0, result.Options.MaxBoostLimitDb, 12);
        // Two numbers, distinguishable: the requested limit and the achieved boost, each formatted from the report.
        Assert.Contains($"Requested limit {S(result.Constraint.MaxBoostLimitDb)} dB", panel.BoostText, StringComparison.Ordinal);
        string achieved = result.Constraint.MaxAchievedBoostDb is { } value
            ? $"achieved boost {S(value)} dB"
            : "no boost was achieved";
        Assert.Contains(achieved, panel.BoostText, StringComparison.Ordinal);
        Assert.DoesNotContain("SPL", panel.BoostText, StringComparison.Ordinal);
        Assert.Contains(result.Constraint.Binding ? "limit bound the search" : "limit did not bind", panel.BindingText, StringComparison.Ordinal);
        Assert.NotEmpty(panel.VerdictText);
        Assert.NotEmpty(panel.DiagnosisLines);

        // The engine call is synchronous and thread-free (ThreadFreeLayersTests bans a Task-returning surface there);
        // the asynchrony is the panel's, so the busy window's notifications come from off the caller's thread. That the
        // continuation is marshalled back onto the dispatcher is the SAME pattern M2 pins against a real dispatcher.
        Assert.NotEmpty(threads);
        Assert.Contains(threads, thread => thread != caller);
    }

    [Fact]
    public void A_real_model_check_reaches_the_panel_with_the_engines_own_causes()
    {
        // §21's pipeline, not a hand-built list: a real AbValidation.Compare against a deliberately defective measured
        // A+B pass, and the causes it reports are the causes the panel renders. Recipes as measured by the lead:
        // +2.5 dB → Gain, inverted → Polarity, −20° → PhaseSetting, +3 dB below 40 Hz → DeviceDsp,
        // +1.8 dB across the top half → MeasurementSync.
        DualSubMeasurement room = OptimizationTestData.Room();
        (string Name, AbCheckKind Cause, Func<double, int, Complex> Factor)[] defects =
        [
            ("+2.5 dB everywhere", AbCheckKind.Gain, (_, _) => Complex.FromPolarCoordinates(Db(2.5), 0.0)),
            ("inverted", AbCheckKind.Polarity, (_, _) => new Complex(-1.0, 0.0)),
            ("−20° everywhere", AbCheckKind.PhaseSetting, (_, _) => Complex.FromPolarCoordinates(1.0, -20.0 * Math.PI / 180.0)),
            ("+3 dB below 40 Hz", AbCheckKind.DeviceDsp, (f, _) => Complex.FromPolarCoordinates(f < 40.0 ? Db(3.0) : 1.0, 0.0)),
            ("+1.8 dB over the top half", AbCheckKind.MeasurementSync, (_, k) => Complex.FromPolarCoordinates(k * 2 >= 40 ? Db(1.8) : 1.0, 0.0)),
        ];

        var panel = new OptimizerPanelViewModel();
        foreach ((string name, AbCheckKind expected, Func<double, int, Complex> factor) in defects)
        {
            IReadOnlyList<PositionResponse> realAb = Defect(SubwooferModel.Combine(room, SubwooferSetting.Baseline), factor);
            panel.UseMeasurement(room with { AB = realAb });
            Assert.True(panel.CanRun);                                   // a measurement went in through the panel's own path
            panel.ValidateModel();

            AbValidationResult result = AbValidation.Compare(room, realAb, SubwooferSetting.Baseline);
            Assert.Contains(expected, result.Checks);                    // the engine's finding, restated in the test
            Assert.Contains(panel.CauseLines, line => line.StartsWith(CausePrefix(expected), StringComparison.Ordinal));
            Assert.Contains(result.Verdict.ToString(), panel.ModelVerdictText, StringComparison.Ordinal);
            output.WriteLine($"{name}: {string.Join(", ", result.Checks)} → {string.Join(" / ", panel.CauseLines)}");
        }

        // The rendering is exhaustive over the engine's type: a cause added there cannot silently miss this panel.
        AbCheckKind[] causes = [.. Enum.GetValues<AbCheckKind>()];
        Assert.NotEmpty(causes);                                          // vacuity guard
        var texts = new List<string>();
        foreach (AbCheckKind cause in causes)
        {
            string text = OptimizerPanelViewModel.CauseText(cause);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("unrecognised cause", text, StringComparison.Ordinal);
            texts.Add(text);
        }

        Assert.Equal(causes.Length, texts.Distinct().Count());             // each cause has its own wording

        // The negative twin: nothing matched means the panel says so, and never invents a cause.
        panel.ShowCauses([]);
        Assert.Equal(["no structured cause reported"], panel.CauseLines);
        Assert.DoesNotContain(panel.CauseLines, line => line.Contains("check", StringComparison.Ordinal));
    }

    [Fact]
    public void The_optimizer_tab_renders_the_panel_and_offers_exactly_the_three_limits()
    {
        IReadOnlyList<object> offered = [];
        bool selected = false;
        bool visible = false;
        RenderedImage image = RenderHarness.Render(
            () =>
            {
                ShellView shell = Shell(new FakeAudioBackend());
                // The tab is selected FIRST: an unselected tab's content is never loaded, so its controls and bindings
                // do not exist and a fact that asserts without selecting would be asserting against nothing.
                shell.TabStrip.SelectedIndex = 4;
                selected = shell.TabStrip.SelectedItem is System.Windows.Controls.TabItem { Header: "Optimize" };
                shell.Measure(new System.Windows.Size(960.0, 640.0));
                shell.Arrange(new System.Windows.Rect(0.0, 0.0, 960.0, 640.0));
                shell.UpdateLayout();
                offered = [.. shell.LimitBox.Items.Cast<object>()];
                visible = shell.OptimizePanel.Visibility == System.Windows.Visibility.Visible;
                return shell;
            },
            960.0,
            640.0);

        Assert.True(selected);
        Assert.True(visible);
        // Condition 3 at render level: the control itself offers exactly the three legal limits.
        Assert.Equal(3, offered.Count);
        Assert.Equal([0.0, 3.0, 6.0], offered.Cast<double>().Order().ToArray());
        Assert.Contains(image.Texts, text => text.Contains("Max boost", StringComparison.Ordinal));
        Assert.Contains(image.Texts, text => text.Contains("Run search", StringComparison.Ordinal));
        Assert.Contains(image.Texts, text => text.Contains("Structured causes", StringComparison.Ordinal));
        // The panel was refreshed by the tab selection, so the status line comes from the session it read.
        Assert.Contains(image.Texts, text => text.Contains("position(s) ready", StringComparison.Ordinal));
        Assert.DoesNotContain(image.Texts, text => text.Contains("SPL", StringComparison.Ordinal));
        output.WriteLine(string.Join(" | ", image.Texts));
    }

    /// <summary>The panel's own fixed-point convention, restated rather than shared, so both numbers are pinned here.</summary>
    private static string S(double value) => value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);

    private static double Db(double decibels) => Math.Pow(10.0, decibels / 20.0);

    private static string CausePrefix(AbCheckKind cause) => cause switch
    {
        AbCheckKind.Polarity => "polarity",
        AbCheckKind.Gain => "gain",
        AbCheckKind.PhaseSetting => "phase",
        AbCheckKind.DeviceDsp => "device DSP",
        _ => "measurement sync",
    };

    /// <summary>Applies a per-frequency complex factor to every bin, i.e. a defect on the real A+B pass.</summary>
    private static IReadOnlyList<PositionResponse> Defect(IReadOnlyList<PositionResponse> positions, Func<double, int, Complex> factor)
        // Constructed through the validating constructor: with all three members get-only there is no `with` rewrite
        // that could build a position whose declared band does not describe its bins.
        => [.. positions.Select(position => new PositionResponse(position.PointId, position.AnalysisBand,
            [.. position.Bins.Select((bin, index) =>
            {
                Complex value = new Complex(bin.Real, bin.Imag) * factor(bin.FrequencyHz, index);
                return OptimizationTestData.Bin(bin.FrequencyHz, value.Real, value.Imaginary);
            })]))];

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

    /// <summary>A shell whose session has A and B measured at both heights, so the panel has positions to read.</summary>
    private static ShellView Shell(FakeAudioBackend backend)
    {
        var shell = new ShellView(backend);
        string directory = Seed("shell");
        shell.Flow.ProjectDirectory = directory;
        shell.Flow.CountX = 1;
        shell.Flow.CountY = 1;
        shell.Flow.CountZ = 2;
        shell.Flow.RefreshDevices();
        shell.Flow.StartSession();
        Assert.NotNull(shell.Flow.Session);
        return shell;
    }

    /// <summary>A and B measured at both heights, so the search has paired positions to work with.</summary>
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
        foreach (MeasurementSlot slot in session.Slots.Where(slot => slot.Mode != SubMode.AB).ToList())
        {
            MeasurementRunOutcome outcome = runner.Run(slot);
            Assert.Equal(MeasurementSlotState.Done, outcome.Slot.State);
        }

        return directory;
    }
}
