// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【名称迁移】旧设置、扩展选择和历史声明恢复为原生工具名，模型只接收当前原生声明。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ToolNames_MigrateSelectionsAndHistoryWithoutRewritingOldMessages()
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent");
        Directory.CreateDirectory(Path.Combine(agent, "extensions"));
        File.WriteAllText(Path.Combine(agent, "extensions", "legacy.js"), """
            export default pi=>pi.registerCommand('legacy',{handler:()=>{
              pi.setActiveTools(['read_file','shell','glob']);
              if(pi.getActiveTools().join(',')!=='read,bash,find')throw Error('legacy activation');
              pi.appendEntry('legacy_names',pi.getActiveTools());
            }});
            """);
        File.WriteAllText(Path.Combine(agent, "coding-agent-settings.json"), """{"defaultTools":["read_file","shell","glob"]}""");
        string[]? declarations = null;
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = false, ModelCatalog = CreateModelCatalog(),
            ProviderId = "test-provider", ModelId = "test-model", ApiKey = "synthetic",
            ProviderRegistry = CreatePromptCapturingRegistry(context => declarations = Transcript.ResolveContext(context).Tools!.Select(tool => tool.Name).ToArray())
        });
        Assert.Equal(["read", "bash", "find"], session.Runner.GetActiveToolNames());
        Assert.Equal(["read", "write", "edit", "bash", "powershell", "find", "grep", "ls"], session.Runner.GetRegisteredTools().Select(tool => tool.Name));
        await foreach (var ignored in session.RunAsync("/legacy")) { }
        Assert.Single(session.TreeSessionController!.Store.ReadExtensionSnapshot().Entries, entry => entry.CustomType == "legacy_names");
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var oldSystem = new SystemMessage("Old session")
        {
            ToolsAdded = new[] { "read_file", "shell", "glob" }.Select(name => new Tool(name, name, schema.RootElement.Clone())).ToArray()
        };
        session.Runner.RestoreSession(new([oldSystem, new UserMessage("old input")], null, null, null));
        Assert.Equal(["read", "bash", "find"], session.Runner.GetActiveToolNames());
        Assert.Same(oldSystem, session.Messages[0]);
        await foreach (var ignored in session.RunAsync("continue")) { }
        Assert.Equal(["read", "bash", "find"], Assert.IsType<string[]>(declarations));
        Assert.Equal(["read_file", "shell", "glob"], oldSystem.ToolsAdded.Select(tool => tool.Name));
        Assert.Equal(["read", "grep"], RuntimeCodingAgentRunner.CreateDefaultTools(selectedBuiltInToolNames: ["read_file", "grep"]).Select(tool => tool.Name));
    }

    /// <summary>【CodingAgent】【自定义名称】旧别名恰好是实际自定义工具名称时，显式选择不能被内置工具抢占。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ToolNames_PreserveRegisteredCustomAliasNames()
    {
        using var temp = TempDirectory.Create();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), NoSession = true, ModelCatalog = CreateModelCatalog(),
            CustomTools = [new StaticAgentTool("read_file")], Tools = ["read_file"]
        });
        Assert.Equal("read_file", Assert.Single(session.Runner.GetRegisteredTools()).Name);
        Assert.Equal(["read_file"], session.Runner.GetActiveToolNames());
        session.Runner.SetActiveTools(["read_file"]);
        Assert.Equal(["read_file"], session.Runner.GetActiveToolNames());
    }
}
