// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【SDK分层】真实会话启动及重载使用全局和项目工具增减配置，设置修改只保存全局差量。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ToolSelection_CombinesGlobalAndProjectModifiersAcrossReload()
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent");
        var project = Path.Combine(temp.Path, ".tau");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(project, "extensions"));
        var globalPath = Path.Combine(agent, "coding-agent-settings.json");
        var projectPath = Path.Combine(project, "coding-agent-settings.json");
        File.WriteAllText(globalPath, """{"defaultTools":["read","+grep"],"defaultThinkingLevel":"high","retry":{"provider":{"timeoutMs":1500}}}""");
        File.WriteAllText(projectPath, """{"defaultTools":["-read","+find"],"retry":{"provider":{"maxRetries":2}}}""");
        File.WriteAllText(Path.Combine(project, "extensions", "reload.js"), "export default pi=>pi.registerCommand('reload',{handler:async(_,ctx)=>{await ctx.reload();}});");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog()
        });
        Assert.Equal(["grep", "find"], session.Runner.GetActiveToolNames());
        Assert.Equal("high", session.SettingsStore.Load().DefaultThinkingLevel);
        var provider = session.SettingsStore.Load().Retry!.Value.GetProperty("provider");
        Assert.Equal(1500, provider.GetProperty("timeoutMs").GetInt32());
        Assert.Equal(2, provider.GetProperty("maxRetries").GetInt32());
        session.SettingsStore.Save(session.SettingsStore.Load() with { Theme = "custom" });
        Assert.Equal("custom", new CodingAgentSettingsStore(globalPath).Load().Theme);
        Assert.Null(new CodingAgentSettingsStore(projectPath).Load().Theme);
        File.WriteAllText(projectPath, """{"defaultTools":["-read","+find","+ls"]}""");
        await foreach (var ignored in session.RunAsync("/reload")) { }
        Assert.Equal(["grep", "find", "ls"], session.Runner.GetActiveToolNames());
    }

    /// <summary>【CodingAgent】【工具选择】默认设置、允许名单和禁用模式约束初始集合及运行中注册。</summary>
    /// <param name="scenario">本次选择规则场景。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("default")]
    [InlineData("empty-defaults")]
    [InlineData("custom-defaults")]
    [InlineData("allow")]
    [InlineData("allow-overrides-all")]
    [InlineData("no-all")]
    [InlineData("no-builtin")]
    public async Task ToolSelection_AppliesPoliciesToInitialAndDynamicTools(string scenario)
    {
        using var temp = TempDirectory.Create();
        var directory = Path.Combine(temp.Path, ".tau", "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tools.js"), """
            export default pi=>{
              const tool=(name,defaultActive=true)=>({name,defaultActive,parameters:{type:'object'},execute:async()=>({content:[]})});
              pi.registerTool(tool('direct')); pi.registerTool(tool('off',false)); pi.registerTool(tool('denied'));
              pi.registerCommand('register',{handler:()=>{
                pi.registerTool(tool('late',false)); pi.registerTool(tool('blocked')); pi.registerTool(tool('denied'));
              }});
              pi.registerCommand('reload',{handler:async(_,ctx)=>{await ctx.reload();}});
            };
            """);
        IReadOnlyList<string>? defaults = scenario switch { "empty-defaults" => [], "custom-defaults" => ["grep", "off"], _ => null };
        var settings = new CodingAgentSettingsStore(Path.Combine(temp.Path, ".tau", "coding-agent-settings.json"));
        settings.Save(new(null, null) { DefaultTools = defaults });
        Assert.Equal(defaults, settings.Load().DefaultTools);
        var allowed = scenario is "allow" or "allow-overrides-all" ? new[] { "off", "late", "denied" } : null;
        var mode = scenario switch
        {
            "allow-overrides-all" or "no-all" => CodingAgentSdkNoToolsMode.All,
            "no-builtin" => CodingAgentSdkNoToolsMode.BuiltIn,
            _ => CodingAgentSdkNoToolsMode.None
        };
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), NoSession = true,
            ModelCatalog = CreateModelCatalog(), NoTools = mode, Tools = allowed, ExcludeTools = ["denied"]
        });
        string[] initial = scenario switch
        {
            "default" => ["read", "bash", "edit", "write", "direct"],
            "empty-defaults" or "no-builtin" => ["direct"],
            "custom-defaults" => ["grep", "off", "direct"],
            "allow" or "allow-overrides-all" => ["off"],
            _ => []
        };
        Assert.Equal(initial, session.Runner.GetActiveToolNames());
        Assert.DoesNotContain(session.Runner.GetRegisteredTools(), tool => tool.Name == "denied");
        if (mode == CodingAgentSdkNoToolsMode.BuiltIn) Assert.Contains(session.Runner.GetRegisteredTools(), tool => tool.Name == "read");
        if (allowed is not null) Assert.Equal("off", Assert.Single(session.Runner.GetRegisteredTools()).Name);
        await foreach (var ignored in session.RunAsync("/register")) { }
        var next = allowed is not null ? initial.Concat(["late"]).ToArray() : mode == CodingAgentSdkNoToolsMode.All ? initial : initial.Concat(["blocked"]).ToArray();
        Assert.Equal(next, session.Runner.GetActiveToolNames());
        Assert.DoesNotContain(session.Runner.GetRegisteredTools(), tool => tool.Name == "denied");
        if (allowed is not null) Assert.DoesNotContain(session.Runner.GetRegisteredTools(), tool => tool.Name == "blocked");
        await foreach (var ignored in session.RunAsync("/reload")) { }
        await foreach (var ignored in session.RunAsync("/register")) { }
        Assert.Equal(next, session.Runner.GetActiveToolNames());
    }

    /// <summary>【CodingAgent】【设置重载】新增默认工具在重载时启用，删除默认配置不关闭已活动工具。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ToolSelection_ReloadActivatesNewDefaultsWithoutRemovingExistingTools()
    {
        using var temp = TempDirectory.Create();
        var directory = Path.Combine(temp.Path, ".tau", "extensions"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "reload.js"), "export default pi=>pi.registerCommand('reload',{handler:async(_,ctx)=>{await ctx.reload();}});");
        var settings = new CodingAgentSettingsStore(Path.Combine(temp.Path, ".tau", "coding-agent-settings.json"));
        settings.Save(new(null, null) { DefaultTools = [] });
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = Path.Combine(temp.Path, "agent"), NoSession = true, ModelCatalog = CreateModelCatalog()
        });
        Assert.Empty(session.Runner.GetActiveToolNames());
        settings.Save(settings.Load() with { DefaultTools = ["grep"] });
        await foreach (var ignored in session.RunAsync("/reload")) { }
        Assert.Equal(["grep"], session.Runner.GetActiveToolNames());
        settings.Save(settings.Load() with { DefaultTools = [] });
        await foreach (var ignored in session.RunAsync("/reload")) { }
        Assert.Equal(["grep"], session.Runner.GetActiveToolNames());
    }
}
