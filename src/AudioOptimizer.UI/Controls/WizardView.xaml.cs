namespace AudioOptimizer.UI.Controls;

using System.Windows.Controls;

/// <summary>
/// §24's twelve rows, presentation only: the rows bind to
/// <see cref="ViewModels.MeasurementWizardViewModel.Steps"/>, whose state is derived from the flow. There is no
/// logic here on purpose — a step that needs a decision gets it in the presenter or in the flow, not in a view.
/// </summary>
public partial class WizardView : UserControl
{
    public WizardView() => InitializeComponent();
}
