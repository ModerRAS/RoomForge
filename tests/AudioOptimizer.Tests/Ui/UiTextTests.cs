namespace AudioOptimizer.Tests.Ui;

using AudioOptimizer.UI.Localization;
using Xunit;

[Collection(RenderCollection.Name)]
public sealed class UiTextTests
{
    [Fact]
    public void English_keeps_the_sentences_the_screens_already_use_and_Chinese_says_what_to_do()
    {
        try
        {
            UiText.Use("en");
            Assert.Equal("Optimize", UiText.Get("Nav.Optimize"));
            Assert.Equal("Wizard", UiText.Get("Nav.Wizard"));
            Assert.Equal("Offline Simulation", UiText.Get("Nav.Simulation"));
            Assert.Equal("Offline simulation", UiText.Get("Sim.Title"));
            Assert.Equal("Generate simulation", UiText.Get("Sim.Generate"));
            Assert.Equal("Run search", UiText.Get("Opt.Run"));
            Assert.Equal("Max boost (dB)", UiText.Get("Opt.Boost"));
            Assert.Equal("Structured causes (§21)", UiText.Get("Opt.Causes"));
            Assert.Contains("measure each position with Space or Enter", UiText.Get("Measure.Help"), StringComparison.Ordinal);
            Assert.Equal("{0} slots: {1} done, {2} invalid, {3} skipped, {4} pending", UiText.Get("Project.StatusLine"));
            Assert.Equal("6 slots: 1 done, 1 invalid, 1 skipped, 3 pending", UiText.Format("Project.StatusLine", 6, 1, 1, 1, 3));
            Assert.Equal("1 input(s), 1 output(s).", UiText.Format("Flow.DeviceCounts", 1, 1));
            Assert.Equal("next", UiText.Get("Wizard.Next"));
            Assert.Equal("Step 6 of 12: Generate points — go", UiText.Format("Wizard.Guide", 6, 12, "Generate points", "go"));

            UiText.Use("zh");
            Assert.Equal("建议", UiText.Get("Nav.Optimize"));
            Assert.Equal("步骤", UiText.Get("Nav.Wizard"));
            Assert.Contains("麦克风", UiText.Get("Measure.Help"), StringComparison.Ordinal);
            Assert.Contains("空格", UiText.Get("Measure.Help"), StringComparison.Ordinal);
            Assert.DoesNotContain("measure each position", UiText.Get("Measure.Help"), StringComparison.Ordinal);
            Assert.Equal("共 6 条记录：1 条完成，1 条无效，1 条跳过，3 条还没测", UiText.Format("Project.StatusLine", 6, 1, 1, 1, 3));
            Assert.Equal("第 6 步，共 12 步：生成测点。去测量页点开始。", UiText.Format("Wizard.Guide", 6, 12, "生成测点", "去测量页点开始。"));
        }
        finally
        {
            UiText.Use("en");
        }
    }
}
