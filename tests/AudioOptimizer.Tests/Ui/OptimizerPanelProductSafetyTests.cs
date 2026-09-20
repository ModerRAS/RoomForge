namespace AudioOptimizer.Tests.Ui;

using AudioOptimizer.Optimization;
using AudioOptimizer.UI.ViewModels;

/// <summary>
/// The product path runs the USER-FACING mode: <see cref="OptimizerOperatingMode.ProductSafety"/> caps the effective
/// limit at 3 dB whatever the panel's closed 0/3/6 set was asked for, the returned recommendation is contract-checked
/// from the measured input, and a capped request is stated rather than silent. Before this wiring the panel passed the
/// raw 6 dB to the search and no production call site ever used <see cref="BoostPolicy"/>, so the product-safety
/// ceiling existed only in tests.
/// </summary>
[Collection(RenderCollection.Name)]
public class OptimizerPanelProductSafetyTests
{
    [Fact]
    public async Task A_six_db_request_runs_in_product_safety_and_stays_contract_clean()
    {
        var panel = new OptimizerPanelViewModel { MaxBoostDb = 6.0 };
        DualSubMeasurement input = OptimizationTestData.Room();
        panel.UseMeasurement(input);

        await panel.RunAsync();

        Assert.NotNull(panel.Result);
        Assert.Equal(BoostPolicy.ProductSafetyMaxBoostDb, panel.Result!.Constraint.MaxBoostLimitDb, 12);
        BoostContractReport contract = BoostPolicy.Verify(input, panel.Result, OptimizerOperatingMode.ProductSafety);
        Assert.False(contract.Violated, contract.Status);
        Assert.True(contract.ReportedValueDescribesFinalRecommendation, contract.Status);
        Assert.Contains("product-safety", panel.Status, StringComparison.OrdinalIgnoreCase);
    }
}
