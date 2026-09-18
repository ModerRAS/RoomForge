namespace AudioOptimizer.UI.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioOptimizer.Audio;
using AudioOptimizer.UI.ViewModels;
using AudioOptimizer.Visualization;

/// <summary>
/// The shell: presentation only. The project panel binds to <see cref="Project"/> and the measurement panel to
/// <see cref="Flow"/>; the only logic here is gluing the two on a load — a loaded project's grid and sweep are
/// handed to the flow so that resuming cannot be refused for a mismatch the user never chose — plus the
/// try/catch that turns an unanticipated failure into a message instead of a crashed window.
/// <para>
/// Constructing the backend opens no device (WASAPI endpoints are opened and disposed per call), and nothing here
/// enumerates devices on load: the device list is read only when the user presses Refresh devices.
/// </para>
/// </summary>
public partial class ShellView : UserControl
{
    public ShellView()
        : this(new WasapiAudioBackend())
    {
    }
    /// <summary>
    /// The device layer is constructor-injected so the flow's off-thread, failure and abort paths can be tested
    /// without hardware. The parameterless constructor above is what XAML instantiates.
    /// </summary>
    public ShellView(IAudioBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        InitializeComponent();
        Flow = new MeasurementFlowViewModel(backend);
        DataContext = Flow;                 // the measurement panel
        ProjectPanel.DataContext = Project;
        AnalysisPanel.DataContext = Analysis;
        HeatmapPanel.DataContext = Heatmaps;
        OptimizerPanel.DataContext = Optimizer;
        // §24: the wizard presents the flow, so it is constructed from the two panels it reads and owns no data of
        // its own. Nothing refreshes it on tab selection: it re-reads its steps when either panel notifies.
        Wizard = new MeasurementWizardViewModel(Flow, Optimizer);
        WizardPanel.DataContext = Wizard;
        SimulationPanel.DataContext = Simulation;
        // The page's figures are drawn from the view model's own plot objects, exactly as the analysis and heatmap
        // panels' are: no physics and no formatting lives in this file.
        Simulation.PropertyChanged += (_, _) => RenderSimulation();
        // The limit is a fixed set of three values, so the control is populated from the set itself: a fourth value
        // cannot be selected because it is not in the control, and the view model refuses one anyway.
        MaxBoostBox.ItemsSource = OptimizerPanelViewModel.AllowedMaxBoostDb;
        MaxBoostBox.SelectedItem = Optimizer.MaxBoostDb;
        Tabs.SelectionChanged += OnTabChanged;
        // Any state change in the analysis panel (mode, interpolation, show-all-positions) redraws the figures; the
        // session itself is re-read on tab selection only, because reading it recomputes frequency responses.
        Analysis.PropertyChanged += (_, _) => RenderCharts();
        Heatmaps.PropertyChanged += (_, _) => RenderHeatmaps();

        // §25: Space and Enter both measure the current point — this UI has no positioning hardware, so the
        // keyboard is how a run is driven. Built from the command object rather than bound in XAML because
        // InputBindings live outside the visual tree and never inherit the DataContext.
        InputBindings.Add(new KeyBinding(Flow.MeasureCommand, Key.Space, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(Flow.MeasureCommand, Key.Enter, ModifierKeys.None));
    }

    public ProjectLoadViewModel Project { get; } = new();

    public MeasurementFlowViewModel Flow { get; }

    public AnalysisViewModel Analysis { get; } = new();

    public HeatmapViewModel Heatmaps { get; } = new();

    /// <summary>The plane views, one per analysed height: rebuilt from the view model's own plane list.</summary>
    public IReadOnlyList<HeatmapView> PlaneViews => [.. PlaneHost.Children.OfType<HeatmapView>()];

    public OptimizerPanelViewModel Optimizer { get; } = new();

    /// <summary>§24's twelve guided steps, presented over <see cref="Flow"/> and <see cref="Optimizer"/>.</summary>
    public MeasurementWizardViewModel Wizard { get; }

    /// <summary>
    /// The offline lab. It measures nothing and opens nothing: the run is the page's own button, off the UI thread,
    /// through a virtual audio backend.
    /// </summary>
    public SimulationPanelViewModel Simulation { get; } = new();

    /// <summary>The optimizer panel's content element, for render tests that must select its tab first.</summary>
    public FrameworkElement OptimizePanel => OptimizerPanel;

    /// <summary>The max-boost control, so a render test can read the offered values off the control itself.</summary>
    public ComboBox LimitBox => MaxBoostBox;

    /// <summary>
    /// The tab strip. Generated x:Name fields are internal to this assembly, and which tab is showing is state the
    /// render tests must set before layout (an unselected tab's content is never loaded, so its bindings do not exist).
    /// </summary>
    public TabControl TabStrip => Tabs;

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(Tabs.SelectedItem, AnalysisTab)) RefreshAnalysis();
        if (ReferenceEquals(Tabs.SelectedItem, HeatmapTab)) RefreshHeatmaps();
        if (ReferenceEquals(Tabs.SelectedItem, OptimizeTab)) RefreshOptimizer();
    }

    /// <summary>Reads the flow's current session into the analysis panel and draws the four figures.</summary>
    private void RefreshAnalysis()
    {
        try
        {
            Analysis.Refresh(Flow);
        }
        catch (Exception exception)
        {
            Analysis.ReportFailure($"Analysis failed: {exception.Message}");
        }

        RenderCharts();
    }

    /// <summary>Reads the flow's current session into the heatmap panel and draws the planes.</summary>
    private void RefreshHeatmaps()
    {
        try
        {
            Heatmaps.Refresh(Flow);
        }
        catch (Exception exception)
        {
            Heatmaps.ReportFailure($"Heatmaps failed: {exception.Message}");
        }

        RenderHeatmaps();
    }

    /// <summary>Reads the flow's session into the optimizer panel. No search runs here — the run is the user's click.</summary>
    private void RefreshOptimizer()
    {
        try
        {
            Optimizer.Refresh(Flow);
        }
        catch (Exception exception)
        {
            Optimizer.ReportFailure($"The optimizer panel could not read the session: {exception.Message}");
        }
    }

    private void RenderHeatmaps()
    {
        PlaneHost.Children.Clear();
        foreach (Visualization.Heatmap plane in Heatmaps.Planes)
        {
            var view = new HeatmapView();
            view.Show(plane);
            PlaneHost.Children.Add(view);
        }

        if (Heatmaps.PositionMap is { } map)
        {
            var view = new HeatmapView();
            view.Show(map);
            PositionMapHost.Content = view;
        }
        else
        {
            PositionMapHost.Content = null;
        }
    }

    /// <summary>Draws whatever the simulation page's view model now holds — including nothing, which clears the figures.</summary>
    private void RenderSimulation()
    {
        SimulationChartHost.Children.Clear();
        foreach (CurvePlot plot in Simulation.Plots)
        {
            var chart = new CurveChart();
            chart.Show(plot);
            SimulationChartHost.Children.Add(chart);
        }

        SimulationPlaneHost.Children.Clear();
        foreach (Visualization.Heatmap plane in Simulation.Planes)
        {
            var view = new HeatmapView();
            view.Show(plane);
            SimulationPlaneHost.Children.Add(view);
        }
    }

    private void RenderCharts()
    {
        Draw(LevelsChart, Analysis.LevelsPlot);
        Draw(StdDevChart, Analysis.StdDevPlot);
        Draw(RangeChart, Analysis.RangePlot);
        Draw(OverlayChart, Analysis.OverlayPlot);
    }

    private void Draw(CurveChart chart, CurvePlot? plot)
    {
        if (plot is null)
        {
            chart.Clear();
            return;
        }

        chart.Show(plot, Analysis.ShowAllPositions);
    }

    private void OnLoadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Project.Load();
            if (Project is { IsLoaded: true, LoadedManifest: { } manifest })
                Flow.UseProject(Project.ProjectDirectory, manifest);
            RefreshAnalysis();
            RefreshHeatmaps();
            RefreshOptimizer();
        }
        catch (Exception exception)
        {
            Project.ReportFailure(exception.Message);
        }
    }
}
