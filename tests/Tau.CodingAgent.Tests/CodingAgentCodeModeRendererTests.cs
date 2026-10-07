// 作者：xxx
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Components;
using Tau.Tui.Rendering;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【脚本折叠显示】源码按视觉行折叠，保留最近八项调用，费用汇总包含隐藏调用，并兼容两种协议头布局。</summary>
    /// <param name="separateHeader">协议头是否独立占据文本块。</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CodeModeRendererPreviewsCodeCallsAndOutputByVisualLines(bool separateHeader)
    {
        var args = new JsonObject { ["code"] = string.Join("\n", Enumerable.Range(0, 12).Select(index => $"const line{index}=1;")) };
        var calls = new JsonArray(Enumerable.Range(0, 12).Select(index => (JsonNode)new JsonObject
        { ["name"] = $"call{index:D2}", ["args"] = new string('a', 90), ["status"] = index == 0 ? "error" : "ok",
            ["error"] = index == 0 ? "first failure\nsecond failure" : null, ["durationMs"] = 1250, ["cost"] = 0.0001 }).ToArray());
        var details = new JsonObject { ["calls"] = calls, ["fullOutputPath"] = "fixture-output.txt" };
        const string header = "Script completed\nWall time 0.5 seconds\nOutput:\n";
        var output = new string('x', 700) + "END_OF_OUTPUT";
        var content = separateHeader ? new TuiToolResultBlock[] { new TuiToolTextBlock(header), new TuiToolTextBlock(output) } :
            [new TuiToolTextBlock(header + output)];
        var context = new TuiToolExecutionRenderContext(args, new(content, Details: details), false, false, false, "Ctrl+O to expand", 24);
        var collapsed = string.Join("\n", CodingAgentCodeModeRenderer.Render(context));
        Assert.Contains("const line0=1;", collapsed); Assert.DoesNotContain("const line11=1;", collapsed);
        Assert.Contains("4 earlier calls", collapsed); Assert.DoesNotContain("call00", collapsed); Assert.Contains("call04", collapsed);
        Assert.Contains("1.3s", collapsed); Assert.Contains("$0.0001", collapsed); Assert.Contains("Model calls: $0.0012", collapsed);
        Assert.DoesNotContain("Script completed", collapsed); Assert.DoesNotContain("END_OF_OUTPUT", collapsed);
        Assert.Contains("Full output: fixture-output.txt", collapsed); Assert.Contains("Ctrl+O to expand", collapsed);
        var expanded = string.Join("\n", CodingAgentCodeModeRenderer.Render(context with { Expanded = true, Width = 200 }));
        Assert.Contains("const line11=1;", expanded); Assert.Contains("✗ call00", expanded); Assert.Contains("first failure\n    second failure", expanded);
        Assert.Contains(new string('a', 90), expanded); Assert.Contains("END_OF_OUTPUT", expanded); Assert.DoesNotContain("earlier calls", expanded);
    }

    /// <summary>【CodingAgent】【脚本进度显示】运行时显示嵌套状态并隐藏尚未完成的输出，非法参数也能稳定渲染。</summary>
    [Fact]
    public void CodeModeRendererShowsPartialCallsWithoutPartialOutput()
    {
        var context = new TuiToolExecutionRenderContext("{", new([new TuiToolTextBlock("must stay hidden")], Details:
            ParseMcpSessionJson("""{"calls":[{"name":"read","status":"running","args":"{}"},{"name":"cancelled","status":"cancelled"}]}""")),
            false, true, false, "Alt+E to expand", 30);
        var text = string.Join("\n", CodingAgentCodeModeRenderer.Render(context));
        Assert.Contains("[invalid arg]", text); Assert.Contains("… read {}", text); Assert.Contains("⊘ cancelled", text);
        Assert.DoesNotContain("must stay hidden", text);
        var component = new TuiToolExecution("codemode", "fixture", new JsonObject { ["code"] = "const 文本 = 42;\nreturn 文本;" },
            bodyRenderer: value => CodingAgentCodeModeRenderer.Render(value, true), showImages: false);
        component.UpdateResult(new([new TuiToolTextBlock("Script failed\nWall time 1.0 seconds\nOutput:\nScript error:\nfailed")], true));
        var narrow = component.Render(12);
        Assert.All(narrow, line => Assert.Equal(12, TuiText.VisibleWidth(line)));
        Assert.Contains("\u001b[", string.Join("\n", narrow));
        Assert.DoesNotContain("Wall time", string.Join("\n", narrow));
    }

    /// <summary>【CodingAgent】【脚本宿主显示】实际 SDK 嵌套工具事件更新同一个终端组件，最终显示源码、调用状态与脚本输出。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CodeModeRendererIsUsedByInteractiveHostForBuiltinTool()
    {
        using var temp = TempDirectory.Create(); var agent = Path.Combine(temp.Path, "agent");
        File.WriteAllText(Path.Combine(temp.Path, "fixture.txt"), "fixture content");
        var provider = new CodeModeSessionProvider("const value = await tools.read({path:'fixture.txt'}); text('visible script output');");
        var registry = new ProviderRegistry(); registry.Register("sdk-prompt-capture", () => provider, "test");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, IncludeExtensions = false, EnableCodeMode = true,
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = registry, ProviderId = "test-provider", ModelId = "test-model",
            Tools = ["codemode", "read"]
        });
        var terminal = new FakeTerminal(); terminal.QueueInput("run script"); terminal.QueueInput("exit");
        var ui = new InteractiveConsoleSession(terminal);
        await new CodingAgentHost(ui, session.Runner).RunAsync();
        var entry = Assert.Single(ui.Transcript, item => item.Kind == TranscriptEntryKind.Tool);
        Assert.Contains("const value = await tools.read", entry.Text); Assert.Contains("✓ read", entry.Text);
        Assert.Contains("visible script output", entry.Text); Assert.DoesNotContain("Script completed", entry.Text);
        Assert.DoesNotContain("\"code\":", entry.Text);
    }
}
