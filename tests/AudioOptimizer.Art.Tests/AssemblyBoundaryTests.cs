namespace AudioOptimizer.Art.Tests;

public class AssemblyBoundaryTests
{
    [Fact]
    public void Art_references_core_and_dsp_only()
    {
        var names = typeof(PhaseCalibrator).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name).ToHashSet();
        Assert.Contains("AudioOptimizer.Core", names);
        Assert.Contains("AudioOptimizer.Dsp", names);
        Assert.DoesNotContain("AudioOptimizer.Optimization", names);
        Assert.DoesNotContain("AudioOptimizer.UI", names);
        Assert.DoesNotContain("AudioOptimizer.Audio", names);
        Assert.DoesNotContain("AudioOptimizer.Measurement", names);
    }

    [Fact]
    public void Camilla_does_not_reference_the_dual_sub_stack()
    {
        var names = typeof(CamillaExporter).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name).ToHashSet();
        Assert.Contains("AudioOptimizer.Art", names);
        Assert.DoesNotContain("AudioOptimizer.Optimization", names);
        Assert.DoesNotContain("AudioOptimizer.UI", names);
        Assert.DoesNotContain("AudioOptimizer.Audio", names);
        Assert.DoesNotContain("AudioOptimizer.Measurement", names);
    }
}
