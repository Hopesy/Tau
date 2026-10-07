// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【提示来源】启动和重载使用可信项目提示，拒绝项目后始终回退全局文件。</summary>
    /// <param name="trusted">项目是否可信。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SystemPromptFiles_RespectTrustAndReload(bool trusted)
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent");
        var project = Path.Combine(temp.Path, ".tau");
        Directory.CreateDirectory(Path.Combine(agent, "extensions"));
        Directory.CreateDirectory(project);
        foreach (var (directory, scope) in new[] { (agent, "global"), (project, "project") })
        {
            File.WriteAllText(Path.Combine(directory, "SYSTEM.md"), scope + " system");
            File.WriteAllText(Path.Combine(directory, "APPEND_SYSTEM.md"), scope + " append");
        }
        File.WriteAllText(Path.Combine(agent, "extensions", "reload.js"), "export default pi=>pi.registerCommand('reload',{handler:async(_,ctx)=>{await ctx.reload();}});");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(), ProjectTrusted = trusted
        });
        var expected = trusted ? "project" : "global";
        Assert.Equal(expected + " system", session.Runner.GetSystemPromptOptions().CustomPrompt);
        Assert.Equal(expected + " append", session.Runner.GetSystemPromptOptions().AppendSystemPrompt);
        File.WriteAllText(Path.Combine(agent, "SYSTEM.md"), "changed global");
        File.WriteAllText(Path.Combine(project, "SYSTEM.md"), "changed project");
        File.WriteAllText(Path.Combine(agent, "APPEND_SYSTEM.md"), "new global append");
        File.WriteAllText(Path.Combine(project, "APPEND_SYSTEM.md"), "new project append");
        await foreach (var ignored in session.RunAsync("/reload")) { }
        Assert.Equal("changed " + expected, session.Runner.GetSystemPromptOptions().CustomPrompt);
        Assert.Contains("new " + expected + " append", session.Runner.GetSystemPrompt());
        if (trusted)
        {
            File.Delete(Path.Combine(project, "SYSTEM.md"));
            File.Delete(Path.Combine(project, "APPEND_SYSTEM.md"));
            await foreach (var ignored in session.RunAsync("/reload")) { }
            Assert.Equal("changed global", session.Runner.GetSystemPromptOptions().CustomPrompt);
            Assert.Contains("new global append", session.Runner.GetSystemPrompt());
        }
    }

    /// <summary>【CodingAgent】【显式提示】明确指定的文件在未信任项目中也能读取，空追加列表禁用自动发现。</summary>
    [Fact]
    public void SystemPromptFiles_ExplicitSourcesOverrideDiscoveredFiles()
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent");
        Directory.CreateDirectory(agent);
        File.WriteAllText(Path.Combine(agent, "SYSTEM.md"), "global");
        File.WriteAllText(Path.Combine(agent, "APPEND_SYSTEM.md"), "default append");
        File.WriteAllText(Path.Combine(temp.Path, "explicit.md"), "explicit");
        var loader = new CodingAgentSystemPromptFiles(temp.Path, agent, () => false, "explicit.md", []);
        var snapshot = loader.Load();
        Assert.Equal("explicit", snapshot.SystemPrompt);
        Assert.Equal(Path.Combine(temp.Path, "explicit.md"), snapshot.SystemPromptPath);
        Assert.Null(snapshot.AppendSystemPrompt);
        File.WriteAllText(Path.Combine(temp.Path, "explicit.md"), "updated");
        Assert.Equal("updated", loader.Load().SystemPrompt);
        var literal = new CodingAgentSystemPromptFiles(temp.Path, agent, () => false, "literal\ntext", ["first", "explicit.md"]);
        Assert.Equal("literal\ntext", literal.Load().SystemPrompt);
        Assert.Equal("first\n\nupdated", literal.Load().AppendSystemPrompt);
    }

    /// <summary>【CodingAgent】【共享技能】项目技能优先并在 Git 根停止向上发现，拒绝项目后只保留用户技能。</summary>
    [Fact]
    public void Skills_AgentsDirectoriesRespectTrustAndGitBoundary()
    {
        using var temp = TempDirectory.Create();
        var home = Path.Combine(temp.Path, "home");
        var repo = Path.Combine(temp.Path, "repo");
        var cwd = Path.Combine(repo, "nested");
        var user = Path.Combine(temp.Path, "agent", "skills");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(cwd);
        foreach (var (directory, name, body) in new[]
        {
            (Path.Combine(temp.Path, ".agents", "skills"), "outside", "outside"),
            (Path.Combine(repo, ".agents", "skills"), "shared", "repo"),
            (Path.Combine(cwd, ".agents", "skills"), "shared", "nearest"),
            (Path.Combine(home, ".agents", "skills"), "home", "home"),
            (user, "shared", "user"),
            (Path.Combine(cwd, ".tau", "skills"), "project", "project")
        })
        {
            var folder = Path.Combine(directory, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: {body}\n---\n{body}");
        }
        var store = new CodingAgentSkillStore(cwd, user, homeDirectory: home);
        var loaded = store.Load();
        Assert.Equal(["home", "project", "shared"], loaded.Select(skill => skill.Name));
        Assert.Equal("nearest", loaded.Single(skill => skill.Name == "shared").Description);
        store.IsProjectTrusted = false;
        loaded = store.Load();
        Assert.Equal(["home", "shared"], loaded.Select(skill => skill.Name));
        Assert.All(loaded, skill => Assert.Equal("user", skill.Scope));
        Assert.Equal("user", loaded.Single(skill => skill.Name == "shared").Description);
    }
}
