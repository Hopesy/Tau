// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【搜索排名测试】词干、驼峰、停用词、文档长度和稳定排序与上游契约一致。</summary>
    [Fact]
    public void ToolSearchBm25RanksMetadataAndKeepsStableTies()
    {
        Assert.Equal(["http", "request", "issue", "search", "party", "class", "box"], CodingAgentToolSearch.Tokenize("HTTPRequest issues searches parties classes boxes and the"));
        CodingAgentToolSearchDocument[] docs = [new("short", "issue search"), new("long", "issue search " + string.Join(' ', Enumerable.Repeat("other", 30))), new("tie", "issue search"), new("none", "calendar")];
        Assert.Equal(["short", "tie", "long"], CodingAgentToolSearch.Rank("search issues", docs, 8).Select(match => match.Name));
        Assert.Equal("short", Assert.Single(CodingAgentToolSearch.Rank("issues", docs, 1)).Name);
        Assert.Empty(CodingAgentToolSearch.Rank("the and", docs, 8));
        Assert.Empty(CodingAgentToolSearch.Rank("absent", docs, 8));
        Assert.Empty(CodingAgentToolSearch.Rank("issues", docs, 0));
    }

    /// <summary>【CodingAgent】【搜索访问测试】递归 Schema 和命名组可搜索，直接/隐藏/已启用工具不会被再次加载。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ToolSearchLoadsOnlyInactiveDeferredToolsAndValidatesLimits()
    {
        using var temp = TempDirectory.Create();
        var schema = ParseMcpSessionJson("""{"type":"object","properties":{"changes":{"items":{"anyOf":[{"description":"changelog"},{"properties":{"repository":{"description":"sourcecontrol"}}}]}}}}""");
        var ns = ParseMcpSessionJson("""{"name":"workspace","description":"team service","instructions":"patches and revisions"}""");
        SearchFixtureTool[] tools = [new("find_changes", "deferred", schema, ns), new("direct_changes", "direct", schema, ns), new("hidden_changes", "hidden", schema, ns), new("script_changes", "codemode", schema, ns), new("model_changes", "model-only", schema, ns)];
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), NoSession = true, ModelCatalog = CreateModelCatalog(), CustomTools = tools, EnableToolSearch = true, IncludeExtensions = false });
        var search = Assert.IsType<CodingAgentToolSearchTool>(Assert.Single(session.Runner.GetRegisteredTools(), tool => tool.Name == "tool_search"));
        Assert.DoesNotContain("tool_search", session.Runner.GetActiveToolNames());
        var document = CodingAgentToolSearch.CreateDocument(tools[0]);
        Assert.Contains("sourcecontrol", document.Text); Assert.Contains("revisions", document.Text);
        var result = await search.ExecuteAsync("search", ParseMcpSessionJson("""{"query":"sourcecontrol patches","limit":2.0}"""));
        Assert.Equal(["find_changes", "script_changes"], Assert.IsType<JsonElement>(result.Details).GetProperty("loaded").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("find_changes", session.Runner.GetActiveToolNames()); Assert.Contains("script_changes", session.Runner.GetActiveToolNames());
        Assert.DoesNotContain("hidden_changes", session.Runner.GetActiveToolNames());
        Assert.Equal("No matching tools found.", Assert.IsType<TextContent>(Assert.Single((await search.ExecuteAsync("again", ParseMcpSessionJson("""{"query":"patches"}"""))).Content)).Text);
        foreach (var args in new[] { "{\"query\":\" \"}", "{\"query\":\"x\",\"limit\":0}", "{\"query\":\"x\",\"limit\":1.5}" })
            await Assert.ThrowsAsync<ArgumentException>(() => search.ExecuteAsync("invalid", ParseMcpSessionJson(args)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.ExecuteAsync("cancelled", ParseMcpSessionJson("""{"query":"patches"}"""), cancelled.Token));
    }

    /// <summary>【CodingAgent】【搜索限制测试】显式允许/排除限制仍约束发现，SDK 同名工具不能被内置搜索覆盖。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ToolSearchRespectsSessionRestrictionsAndSdkNameOwnership()
    {
        using var temp = TempDirectory.Create();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), NoSession = true, ModelCatalog = CreateModelCatalog(), EnableToolSearch = true,
            CustomTools = [new StaticAgentTool("tool_search")], Tools = ["tool_search"] });
        Assert.IsType<StaticAgentTool>(Assert.Single(session.Runner.GetRegisteredTools()));
        await using var blocked = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), NoSession = true, ModelCatalog = CreateModelCatalog(), EnableToolSearch = true, NoTools = CodingAgentSdkNoToolsMode.All });
        Assert.Empty(blocked.Runner.GetRegisteredTools());
    }

    /// <summary>【CodingAgent】【搜索测试工具】提供不同暴露策略和相同元数据的可比较工具。</summary>
    /// <param name="name">工具名。</param><param name="exposure">暴露策略。</param><param name="schema">输入 Schema。</param><param name="ns">命名组。</param>
    private sealed class SearchFixtureTool(string name, string exposure, JsonElement schema, JsonElement ns) : ICodingAgentToolDefinition
    {
        public string Name => name;
        public string Label => name;
        public string Description => "Inspect changes\nDetailed instructions";
        public string Exposure => exposure;
        public JsonElement ParameterSchema => schema;
        public JsonElement? Namespace => ns;
        /// <summary>【CodingAgent】【测试执行】返回空成功结果。</summary><param name="toolCallId">调用标识。</param><param name="args">参数。</param><param name="ct">取消。</param><param name="onUpdate">进度。</param><returns>成功结果。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null) => Task.FromResult(new ToolResult([]));
    }
}
