namespace AudioOptimizer.Tests;

using AudioOptimizer.Optimization;

/// <summary>
/// SubwooferSetting is a record with a self-typed computed property (<c>Flipped</c>). The compiler-generated
/// PrintMembers prints every public property, so the default ToString built another setting and printed that —
/// recursion until the stack died (<c>InsufficientExecutionStackException</c>). The manual PrintMembers prints
/// the four stored knobs only. This is reachable from tooling (xUnit failure messages and debugger tooltips
/// call ToString) even though no product path logs a raw setting.
/// </summary>
public class SubwooferSettingFormattingTests
{
    [Fact]
    public void ToStringPrintsTheFourKnobsWithoutRecursing()
    {
        SubwooferSetting setting = SubwooferSetting.FromDegrees(-3.0, 60.0, -1, 0.005);

        string text = setting.ToString();

        Assert.StartsWith("SubwooferSetting {", text);
        Assert.Contains("GainDb", text);
        Assert.Contains("PhaseRad", text);
        Assert.Contains("Polarity", text);
        Assert.Contains("DelaySeconds", text);
        Assert.Contains("-1", text);
        Assert.DoesNotContain("Flipped", text);
        Assert.Equal(text, setting.ToString());
    }
}
