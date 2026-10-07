// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentBeforeAgentStartTests
{
    /// <summary>扩展可以读取基础提示与范围副本，临时强制提示和脚本中的原位修改不污染基线。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ContextMetadata_ReturnsIndependentBaseOptionsAndScopedModels()
    {
        using var fixture = new Fixture("""
            export default pi=>{
              const seen=[];
              pi.on('before_agent_start',e=>{e.systemPromptOptions.sections.transient='temporary';return {systemPrompt:'forced'};});
              pi.on('agent_start',(_,ctx)=>{
                const options=ctx.getSystemPromptOptions();
                options.selectedTools.length=0;
                options.sections.changed='local mutation';
                const scoped=ctx.scopedModels;
                scoped[0].model.id='local mutation';
                seen.push({options:ctx.getSystemPromptOptions(),scoped:ctx.scopedModels,thinking:ctx.thinkingLevel,effective:ctx.getSystemPrompt()});
              });
              pi.registerCommand('seen',{handler:()=>JSON.stringify(seen)});
            };
            """);
        var (runner, _) = fixture.CreateRunner();
        runner.SetScopedModels([new(runner.Model, "high")]);
        await foreach (var ignored in runner.RunAsync("first")) { }
        using var seen = JsonDocument.Parse(fixture.Runtime.Invoke(fixture.Files[0], "seen", "").StatusMessage!);
        var current = seen.RootElement[0];
        Assert.Equal("Base", current.GetProperty("options").GetProperty("customPrompt").GetString());
        Assert.Equal("read", Assert.Single(current.GetProperty("options").GetProperty("selectedTools").EnumerateArray()).GetString());
        Assert.Empty(current.GetProperty("options").GetProperty("sections").EnumerateObject());
        Assert.Equal("test", current.GetProperty("scoped")[0].GetProperty("model").GetProperty("id").GetString());
        Assert.Equal("high", current.GetProperty("scoped")[0].GetProperty("thinkingLevel").GetString());
        Assert.Equal(CodingAgentThinkingLevels.Format(runner.ThinkingLevel), current.GetProperty("thinking").GetString());
        Assert.Equal("forced", current.GetProperty("effective").GetString());
        var baseline = runner.GetSystemPromptOptions();
        baseline.Sections["outside"] = "mutation";
        Assert.Empty(runner.GetSystemPromptOptions().Sections);
    }

    /// <summary>未配置范围为空，设置中的通配符与等级可以解析，显式 SDK 空范围优先于设置。</summary>
    [Fact]
    public void ContextMetadata_ResolvesScopedSettingsWithoutInventingDefaultScope()
    {
        using var fixture = new Fixture("export default pi=>{};");
        var (runner, _) = fixture.CreateRunner();
        Assert.Empty(runner.GetScopedModels());
        var store = new CodingAgentSettingsStore(Path.Combine(Path.GetDirectoryName(fixture.Files[0])!, "settings.json"));
        runner.ConfigureSessionSettings(store);
        store.Save(store.Load() with { EnabledModels = ["missing/model", "synthetic/*:high", "synthetic/test:low"] });
        Assert.Equal("high", Assert.Single(runner.GetScopedModels()).ThinkingLevel);
        runner.SetScopedModels([]);
        Assert.Empty(runner.GetScopedModels());
        runner.SetScopedModels(null);
        Assert.Single(runner.GetScopedModels());
    }
}
