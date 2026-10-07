// 作者：xxx
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【宿主动态命名组】同一回调同时驱动声明、搜索排名和命名组过滤，动态模式与预算按回合重新读取。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task CodeModeHostNamespaceAndDynamicPresentationReachScripts()
    {
        using var temp = TempDirectory.Create(); var mode = "only"; var budget = 1000d; var grouped = true;
        var provider = new CodeModeSessionProvider(
            """
            const ns=await describeNamespace('host-group');
            if(!ns||!ns.tools.includes('read')||ns.instructions!=='quasar')throw Error('namespace mismatch');
            const found=await searchTools('quasar',{namespace:'host_group'});
            if(found.length!==1||found[0].name!=='read')throw Error('search namespace ignored');
            text('host namespace');
            """,
            """
            if(await describeNamespace('host-group')!==undefined)throw Error('old namespace leaked');
            if((await searchTools('quasar',{namespace:'host-group'})).length)throw Error('old index leaked');
            text('namespace cleared');
            """);
        var registry = new ProviderRegistry(); registry.Register("sdk-prompt-capture", () => provider, "test");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), NoSession = true,
            ApiKey = "synthetic", ModelCatalog = CreateModelCatalog(), ProviderRegistry = registry, ProviderId = "test-provider", ModelId = "test-model",
            EnableCodeMode = true, IncludeExtensions = false, Tools = ["codemode", "read"],
            CodeModeOptions = new()
            {
                Models = false, Mode = "on", InlineBudget = 0, GetMode = () => mode, GetInlineBudget = () => budget,
                GetToolNamespace = name => grouped && name == "read" ? ParseMcpSessionJson("""{"name":"host-group","description":"host catalog","instructions":"quasar"}""") : null
            }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var ignored in session.RunAsync("first", deadline.Token)) { }
        var first = session.Messages.OfType<ToolResultMessage>().Last();
        Assert.False(first.IsError, string.Concat(first.Content.OfType<TextContent>().Select(block => block.Text)));
        var declaration = Assert.Single(Transcript.GetCurrentTools(provider.Contexts[0].Messages));
        Assert.Contains("## host-group\nhost catalog", declaration.Description);
        Assert.Contains("### \u0060read\u0060", declaration.Description);
        grouped = false; mode = "on"; budget = 0;
        await foreach (var ignored in session.RunAsync("second", deadline.Token)) { }
        var second = session.Messages.OfType<ToolResultMessage>().Last();
        Assert.False(second.IsError, string.Concat(second.Content.OfType<TextContent>().Select(block => block.Text)));
        var declarations = Transcript.GetCurrentTools(provider.Contexts[2].Messages);
        Assert.Contains(declarations, tool => tool.Name == "read");
        Assert.DoesNotContain("host-group", Assert.Single(declarations, tool => tool.Name == "codemode").Description);
        var code = Assert.IsType<CodingAgentCodeModeTool>(session.Runner.GetRegisteredTools().Single(tool => tool.Name == "codemode"));
        var loadout = new CodingAgentToolLoadout([code], [], [code]);
        mode = "invalid"; Assert.Throws<ArgumentException>(() => code.PrepareLoadout(loadout));
        mode = "on"; budget = double.NaN; Assert.Throws<ArgumentOutOfRangeException>(() => code.PrepareLoadout(loadout));
    }

    /// <summary>【CodingAgent】【宿主状态写入】禁用内置保存仍读取分支状态，显式回调只收到成功写入，同条目先删后写。</summary>
    /// <param name="callback">是否提供自定义写入回调。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodeModeHostStoreCallbackAndReadOnlyPersistencePreserveBranchState(bool callback)
    {
        using var temp = TempDirectory.Create(); var entries = new List<(string Type, CodingAgentCodeModeStoreEntryData Data)>();
        var path = Path.Combine(temp.Path, "session.jsonl");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), SessionPath = path,
            ModelCatalog = CreateModelCatalog(), EnableCodeMode = true, IncludeExtensions = false,
            CodeModeOptions = new()
            {
                Models = false, PersistStore = false,
                AppendEntry = callback ? (type, data) => entries.Add((type, data)) : null
            }
        });
        var tree = session.TreeSessionController!;
        tree.Store.AppendExtensionEntry(new()
        {
            Id = Guid.NewGuid().ToString("N"), ParentId = tree.LoadSnapshot().LeafId, Type = "custom",
            Timestamp = DateTimeOffset.UtcNow, CustomType = "codemode-store",
            Data = ParseMcpSessionJson("""{"set":{"same":"seed","gone":1},"delete":["same"]}""")
        });
        tree.LoadSnapshot();
        var code = session.Runner.GetRegisteredTools().Single(tool => tool.Name == "codemode");
        var scripts = new[]
        {
            "if(load('same')!=='seed')throw Error('replay order');store('same','changed');store('gone',undefined);text(load('same'));",
            "if(load('same')!=='seed'||load('gone')!==1)throw Error('callback persisted unexpectedly');store('failed','x');throw Error('expected failure');",
            "if(load('same')!=='seed'||load('failed')!==undefined)throw Error('failed write persisted');text('unchanged');"
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (var index = 0; index < scripts.Length; index++)
        {
            var args = new System.Text.Json.Nodes.JsonObject { ["code"] = scripts[index] };
            var result = await code.ExecuteAsync("host-" + index, ParseMcpSessionJson(args.ToJsonString()), deadline.Token);
            Assert.Equal(index == 1, result.IsError);
            if (index == 1) Assert.Contains("expected failure", string.Concat(result.Content.OfType<TextContent>().Select(block => block.Text)));
        }
        if (callback)
        {
            var entry = Assert.Single(entries); Assert.Equal("codemode-store", entry.Type);
            Assert.Equal("changed", entry.Data.Set["same"]!.GetValue<string>()); Assert.Equal(["gone"], entry.Data.Delete);
        }
        else Assert.Empty(entries);
        Assert.Single(File.ReadAllLines(path).Select(ParseMcpSessionJson), entry =>
            entry.TryGetProperty("customType", out var custom) && custom.GetString() == "codemode-store");
    }
}
