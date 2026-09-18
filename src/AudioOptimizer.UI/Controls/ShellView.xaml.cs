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
