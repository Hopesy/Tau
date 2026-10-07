// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【资源配置】独立解析全局和项目相对路径，自动资源排除在执行前生效，重载替换配置路径。</summary>
    /// <param name="trusted">是否允许项目资源。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResourceSettings_LoadScopedPathsFilterAutoloadAndReload(bool trusted)
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent");
        var project = Path.Combine(temp.Path, ".tau");
        foreach (var (root, name) in new[] { (agent, "global"), (project, "project") })
        {
            var extra = Path.Combine(root, "extra");
            Directory.CreateDirectory(Path.Combine(extra, "skill"));
            Directory.CreateDirectory(Path.Combine(root, "extensions"));
            File.WriteAllText(Path.Combine(extra, "extension.js"), $"export default pi=>pi.registerCommand('{name}-command',{{handler:()=>{{}}}});");
            File.WriteAllText(Path.Combine(extra, "skill", "SKILL.md"), $"---\nname: {name}-skill\ndescription: Scoped skill\n---\nSkill body");
            File.WriteAllText(Path.Combine(extra, name + ".md"), "Scoped template");
            File.WriteAllText(Path.Combine(extra, "theme.json"), "{}");
            File.WriteAllText(Path.Combine(root, "coding-agent-settings.json"), $$"""
                {"extensions":["extra/extension.js","!disabled.js"],"skills":["extra/skill","!hidden"],
                 "prompts":["extra/{{name}}.md","!hidden.md"],"themes":["extra/theme.json"]}
                """);
            File.WriteAllText(Path.Combine(root, "extensions", "disabled.js"), "import fs from 'node:fs'; export default ()=>{fs.writeFileSync('disabled-loaded.flag','bad'); throw Error('excluded module executed');};");
        }
        Directory.CreateDirectory(Path.Combine(agent, "skills", "hidden"));
        Directory.CreateDirectory(Path.Combine(agent, "prompts"));
        File.WriteAllText(Path.Combine(agent, "skills", "hidden", "SKILL.md"), "---\nname: hidden\ndescription: Hidden skill\n---\nBody");
        File.WriteAllText(Path.Combine(agent, "prompts", "hidden.md"), "Hidden prompt");
        File.WriteAllText(Path.Combine(agent, "extensions", "control.js"), "export default pi=>pi.registerCommand('reload',{handler:async(_,ctx)=>{await ctx.reload();}});");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, ModelCatalog = CreateModelCatalog(), ProjectTrusted = trusted });
        var names = session.Runner.GetCommands().Select(command => command.Name).ToArray();
        Assert.Contains("global-command", names);
        Assert.Equal(trusted, names.Contains("project-command"));
        Assert.Contains("global", names);
        Assert.DoesNotContain("hidden", names);
        Assert.DoesNotContain("skill:hidden", names);
        Assert.Equal(trusted ? 2 : 1, session.SkillStore.Load().Count);
        Assert.Equal(trusted ? 2 : 1, session.PackageResourceState.ThemePaths.Count);
        Assert.All(session.SkillStore.Load(), skill => Assert.Equal("local", skill.SourceInfo.Source));
        Assert.False(File.Exists(Path.Combine(temp.Path, "disabled-loaded.flag")));
        Assert.DoesNotContain(session.ExtensionStatus.Diagnostics, item => item.Message.Contains("excluded module"));
        File.WriteAllText(Path.Combine(agent, "extra", "new.js"), "export default pi=>pi.registerCommand('replacement-command',{handler:()=>{}});");
        var settingsPath = Path.Combine(agent, "coding-agent-settings.json");
        File.WriteAllText(settingsPath, File.ReadAllText(settingsPath).Replace("extra/extension.js", "extra/new.js", StringComparison.Ordinal));
        await foreach (var ignored in session.RunAsync("/reload")) { }
        var reloaded = session.Runner.GetCommands().Select(command => command.Name).ToArray();
        Assert.Contains("replacement-command", reloaded);
        Assert.DoesNotContain("global-command", reloaded);
        Assert.Equal(trusted, reloaded.Contains("project-command"));
        Assert.False(File.Exists(Path.Combine(temp.Path, "disabled-loaded.flag")));
    }

    /// <summary>【CodingAgent】【自动规则】强制包含覆盖排除，强制排除具有最终优先级，规则不能意外变为加载路径。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ResourceSettings_ForceIncludeAndExcludeHaveNativePrecedence()
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent");
        Directory.CreateDirectory(Path.Combine(agent, "extensions"));
        foreach (var name in new[] { "yes", "no", "other" })
            File.WriteAllText(Path.Combine(agent, "extensions", name + ".js"), $"export default pi=>pi.registerCommand('{name}',{{handler:()=>{{}}}});");
        File.WriteAllText(Path.Combine(agent, "coding-agent-settings.json"), """
            {"extensions":["!*.js","+extensions/yes.js","+extensions/no.js","-extensions/no.js"]}
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = temp.Path, AgentDirectory = agent, ModelCatalog = CreateModelCatalog() });
        Assert.Equal(["yes"], session.Runner.GetCommands().Select(command => command.Name));
        Assert.Empty(session.PackageResourceState.ExtensionPaths);
    }
}
