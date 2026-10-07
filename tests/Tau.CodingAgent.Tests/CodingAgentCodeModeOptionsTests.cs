// 作者：xxx
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【SDK 脚本选项】关闭模型访问仍保留普通工具和分支存储，并使显式呈现选项优先于文件设置。</summary>
    /// <param name="mode">显式呈现模式。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("on")]
    [InlineData("only")]
    public async Task CodeModeSdkOptionsDisableModelsAndPreserveToolsAndState(string mode)
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent"); Directory.CreateDirectory(agent);
        File.WriteAllText(Path.Combine(agent, "coding-agent-settings.json"),
            mode == "only" ? """{"codemode":{"mode":"on","inlineBudget":1000}}""" : """{"codemode":{"mode":"only","inlineBudget":0}}""");
        File.WriteAllText(Path.Combine(temp.Path, "fixture.txt"), "tool remains available");
        var provider = new CodeModeSessionProvider(
            """
            if(typeof models!=='undefined')throw Error('models leaked');
            store('saved',await tools.read({path:'fixture.txt'}));
            text('stored');
            """,
            """
            if(typeof models!=='undefined')throw Error('models leaked');
            if(!load('saved').includes('tool remains available'))throw Error('state lost');
            text(load('saved'));
            """);
        var registry = new ProviderRegistry(); registry.Register("sdk-prompt-capture", () => provider, "test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = registry,
            ProviderId = "test-provider", ModelId = "test-model", EnableCodeMode = true,
            CodeModeOptions = new() { Models = false, Mode = mode, InlineBudget = mode == "only" ? 0 : 1000 },
            IncludeExtensions = false, IncludeSkills = false, IncludePromptTemplates = false, IncludeContextFiles = false
        }, timeout.Token);
        session.Runner.SetActiveTools(["codemode", "read"]);
        foreach (var prompt in new[] { "save", "load" })
        {
            await foreach (var ignored in session.RunAsync(prompt, timeout.Token)) { }
            var result = session.Messages.OfType<ToolResultMessage>().Last();
            Assert.False(result.IsError, string.Concat(result.Content.OfType<TextContent>().Select(block => block.Text)));
        }
        var declarations = Transcript.GetCurrentTools(provider.Contexts[0].Messages);
        var code = Assert.Single(declarations, tool => tool.Name == "codemode");
        Assert.DoesNotContain("models.getModelsOfType", code.Description);
        Assert.DoesNotContain("### \u0060read\u0060", code.Description);
        if (mode == "only") Assert.Single(declarations);
        else Assert.Contains(declarations, tool => tool.Name == "read" && tool.Description.Contains("Codemode:"));
        Assert.Contains("tool remains available", string.Concat(session.Messages.OfType<ToolResultMessage>().Last().Content.OfType<TextContent>().Select(block => block.Text)));
    }

    /// <summary>【CodingAgent】【SDK 提前校验】非法脚本选项在任何迁移和资源初始化之前失败。</summary>
    /// <param name="mcp">是否由 MCP 自动启用脚本。</param><param name="mode">待校验模式。</param><param name="budget">待校验预算。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false, "off", null)]
    [InlineData(true, "only", -1d)]
    [InlineData(false, null, double.PositiveInfinity)]
    [InlineData(true, null, double.NaN)]
    public async Task CodeModeInvalidSdkOptionsFailBeforeInitialization(bool mcp, string? mode, double? budget)
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "untouched");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, EnableCodeMode = !mcp, EnableMcp = mcp,
            CodeModeOptions = new() { Mode = mode, InlineBudget = budget }
        }));
        Assert.False(Directory.Exists(agent));
    }
}
