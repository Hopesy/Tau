// 作者：xxx
using System.Text.Json;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentSdkTests
{
    /// <summary>【CodingAgent】【信任交互】未绑定会话时扩展可以选择、确认、输入和通知，启动后继续使用同一扩展实例。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ProjectTrust_ExtensionReceivesStartupUi()
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent");
        Directory.CreateDirectory(Path.Combine(agent, "extensions"));
        Directory.CreateDirectory(Path.Combine(temp.Path, ".tau", "extensions"));
        File.WriteAllText(Path.Combine(agent, "extensions", "ui.js"), """
            export default pi=>pi.on('project_trust',async(e,ctx)=>{
              if(!ctx.hasUI||Object.keys(ctx.ui).sort().join(',')!=='confirm,input,notify,select')throw Error('invalid UI');
              if(await ctx.ui.select('pick',['first','second'])!=='second')throw Error('select');
              if(!await ctx.ui.confirm('confirm','message'))throw Error('confirm');
              if(await ctx.ui.input('input','hint')!=='answer')throw Error('input');
              ctx.ui.notify('accepted','info');
              return {trusted:'yes'};
            });
            """);
        var errors = new List<string>();
        var notices = new List<string>();
        var selections = new List<string>();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = true, ModelCatalog = CreateModelCatalog(), ResolveProjectTrust = true,
            ProjectTrustSelector = (title, options, _) =>
            {
                selections.Add(title);
                return Task.FromResult<string?>(options.Contains("second") ? "second" : "Yes");
            },
            ProjectTrustInput = (title, hint, _) =>
            {
                Assert.Equal("input", title); Assert.Equal("hint", hint);
                return Task.FromResult<string?>("answer");
            },
            ProjectTrustError = errors.Add, ProjectTrustNotify = notices.Add
        });
        Assert.True(session.Runner.IsProjectTrusted);
        Assert.Empty(errors);
        Assert.Equal(["pick", "confirm\nmessage"], selections);
        Assert.Equal(["accepted"], notices);
    }

    /// <summary>【CodingAgent】【启动信任】全局扩展先决策，项目扩展不能自行批准；拒绝后重载仍然受限。</summary>
    /// <param name="decision">全局扩展决定。</param><param name="explicitDecision">显式覆盖。</param><param name="expected">最终信任状态。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData("yes", null, true)]
    [InlineData("no", null, false)]
    [InlineData("undecided", null, false)]
    [InlineData("no", true, true)]
    [InlineData("yes", false, false)]
    public async Task ProjectTrust_ControlsSdkResourcesAndReload(string decision, bool? explicitDecision, bool expected)
    {
        using var temp = TempDirectory.Create();
        var agent = Path.Combine(temp.Path, "agent");
        var project = Path.Combine(temp.Path, ".tau");
        Directory.CreateDirectory(Path.Combine(agent, "extensions"));
        Directory.CreateDirectory(Path.Combine(project, "extensions"));
        Directory.CreateDirectory(Path.Combine(project, "prompts"));
        Directory.CreateDirectory(Path.Combine(project, "skills", "project-skill"));
        File.WriteAllText(Path.Combine(project, "prompts", "project-prompt.md"), "Project prompt");
        File.WriteAllText(Path.Combine(project, "skills", "project-skill", "SKILL.md"), "---\nname: project-skill\ndescription: project\n---\nProject skill");
        File.WriteAllText(Path.Combine(agent, "coding-agent-settings.json"), """{"theme":"global","defaultProjectTrust":"ask"}""");
        File.WriteAllText(Path.Combine(project, "coding-agent-settings.json"), """{"theme":"project","defaultProjectTrust":"always"}""");
        File.WriteAllText(Path.Combine(agent, "extensions", "trust.js"), $$$"""
            export default pi=>{
              let checks=0;
              pi.registerTool({name:'global_tool',parameters:{type:'object'},execute:async()=>({content:[]})});
              pi.on('project_trust',()=>{throw Error('isolated trust failure')});
              pi.on('project_trust',()=>({trusted:'undecided'}));
              pi.on('project_trust',(e,ctx)=>{
                if('model' in ctx||ctx.hasUI||ctx.mode!=='print'||ctx.cwd!==e.cwd)throw Error('invalid trust context');
                checks++;
                return {trusted:'{{{decision}}}',remember:true};
              });
              pi.on('session_start',(e,ctx)=>pi.appendEntry('trust_snapshot',{checks,trusted:ctx.isProjectTrusted()}));
              pi.registerCommand('reload',{handler:async(_,ctx)=>{await ctx.reload();}});
            };
            """);
        File.WriteAllText(Path.Combine(project, "extensions", "project.js"), """
            export default pi=>{
              pi.on('project_trust',()=>({trusted:'yes',remember:true}));
              pi.registerTool({name:'project_tool',parameters:{type:'object'},execute:async()=>({content:[]})});
            };
            """);
        var errors = new List<string>();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        {
            Cwd = temp.Path, AgentDirectory = agent, NoSession = false, ModelCatalog = CreateModelCatalog(),
            ResolveProjectTrust = true, ProjectTrusted = explicitDecision, ProjectTrustError = errors.Add
        });
        Assert.Equal(expected, session.Runner.IsProjectTrusted);
        Assert.Equal(expected ? "project" : "global", session.SettingsStore.Load().Theme);
        Assert.Contains(session.Runner.GetRegisteredTools(), tool => tool.Name == "global_tool");
        Assert.Equal(expected, session.Runner.GetRegisteredTools().Any(tool => tool.Name == "project_tool"));
        Assert.Equal(expected, session.SkillStore.Load().Any(skill => skill.Name == "project-skill"));
        Assert.Equal(expected, session.PromptTemplateStore.Load().Any(prompt => prompt.Name == "project-prompt"));
        var entry = Assert.Single(session.TreeSessionController!.Store.ReadExtensionSnapshot().Entries,
            entry => entry.Type == "custom" && entry.CustomType == "trust_snapshot");
        var data = Assert.IsType<JsonElement>(entry.Data);
        Assert.Equal(explicitDecision is null ? 1 : 0, data.GetProperty("checks").GetInt32());
        Assert.Equal(expected, data.GetProperty("trusted").GetBoolean());
        Assert.Equal(explicitDecision is null ? 1 : 0, errors.Count);
        var remembered = new CodingAgentProjectTrustStore(agent).Get(temp.Path);
        Assert.Equal(explicitDecision is null && decision != "undecided" ? expected : (bool?)null, remembered);
        await foreach (var ignored in session.RunAsync("/reload")) { }
        Assert.Equal(expected, session.Runner.GetRegisteredTools().Any(tool => tool.Name == "project_tool"));
    }
}

public sealed class CodingAgentProjectTrustTests
{
    /// <summary>【CodingAgent】【目录继承】最近祖先生效，信任父目录选项移除当前拒绝，兄弟目录不误匹配。</summary>
    [Fact]
    public void Store_InheritsNearestAncestorAndRemovesExplicitOverride()
    {
        using var fixture = new Fixture();
        var store = new CodingAgentProjectTrustStore(fixture.Agent);
        var child = Path.Combine(fixture.Project, "child");
        Directory.CreateDirectory(child);
        store.Set(fixture.Project, true);
        Assert.True(store.Get(child));
        Assert.Null(store.Get(fixture.Project + "-sibling"));
        store.Set(child, false);
        Assert.False(store.Get(child));
        var option = CodingAgentProjectTrust.GetOptions(child).Single(option => option.Label.StartsWith("Trust parent folder"));
        store.SetMany(option.Updates);
        Assert.True(store.Get(child));
        Assert.Equal(CodingAgentProjectTrustStore.NormalizePath(fixture.Project), store.GetEntry(child)!.Path);
        Assert.Equal(5, CodingAgentProjectTrust.GetOptions(child, includeSessionOnly: true).Count);
    }

    /// <summary>【CodingAgent】【损坏记录】非法信任文件既不能读取也不能被保存覆盖。</summary>
    /// <param name="value">损坏文件内容。</param>
    [Theory]
    [InlineData("[]")]
    [InlineData("{broken")]
    [InlineData("{\"path\":\"true\"}")]
    public void Store_RejectsInvalidData(string value)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Agent, "trust.json");
        File.WriteAllText(path, value);
        var store = new CodingAgentProjectTrustStore(fixture.Agent);
        Assert.Throws<InvalidDataException>(() => store.Get(fixture.Project));
        Assert.Throws<InvalidDataException>(() => store.Set(fixture.Project, true));
        Assert.Equal(value, File.ReadAllText(path));
    }

    /// <summary>【CodingAgent】【信任顺序】显式决定和空项目跳过扩展，扩展优先于已存决定，临时 UI 决定不写入文件。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Resolve_OrdersPoliciesAndKeepsSessionOnlyChoicesTemporary()
    {
        using var fixture = new Fixture();
        var store = new CodingAgentProjectTrustStore(fixture.Agent);
        var calls = 0;
        var request = new CodingAgentProjectTrustRequest(fixture.Project, store)
        {
            ExtensionDecision = _ => { calls++; return Task.FromResult<CodingAgentProjectTrustDecision?>(new("no")); },
            DefaultPolicy = "always"
        };
        Assert.True(await CodingAgentProjectTrust.ResolveAsync(request));
        Assert.False(await CodingAgentProjectTrust.ResolveAsync(request with { Override = false }));
        Assert.Equal(0, calls);
        Directory.CreateDirectory(Path.Combine(fixture.Project, ".tau", "extensions"));
        store.Set(fixture.Project, true);
        Assert.False(await CodingAgentProjectTrust.ResolveAsync(request));
        Assert.Equal(1, calls);
        Assert.True(await CodingAgentProjectTrust.ResolveAsync(request with { ExtensionDecision = null, DefaultPolicy = "never" }));
        store.Set(fixture.Project, null);
        Assert.False(await CodingAgentProjectTrust.ResolveAsync(request with { ExtensionDecision = null, DefaultPolicy = "ask" }));
        Assert.True(await CodingAgentProjectTrust.ResolveAsync(request with
        {
            ExtensionDecision = null, DefaultPolicy = "ask",
            Select = (_, options, _) => Task.FromResult<string?>(options.Single(option => option == "Trust (this session only)"))
        }));
        Assert.Null(store.Get(fixture.Project));
    }

    /// <summary>【CodingAgent】【共享技能】用户技能不触发项目提示，其他祖先技能会触发。</summary>
    [Fact]
    public void Resources_ExcludeHomeSkillsAndDetectAncestorSkills()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".agents", "skills"));
        Assert.False(CodingAgentProjectTrust.HasTrustRequiringResources(fixture.Project, fixture.Root));
        Assert.True(CodingAgentProjectTrust.HasTrustRequiringResources(fixture.Project, fixture.Agent));
        Directory.CreateDirectory(Path.Combine(fixture.Project, ".tau"));
        File.WriteAllText(Path.Combine(fixture.Project, ".tau", "SYSTEM.md"), "Instructions");
        Assert.True(CodingAgentProjectTrust.HasTrustRequiringResources(fixture.Project, fixture.Root));
    }

    /// <summary>【CodingAgent】【包限制】拒绝信任后项目包不参与发现，项目修改在访问包后端前失败。</summary>
    [Fact]
    public void Packages_UntrustedProjectIsExcludedAndCannotBeModified()
    {
        using var fixture = new Fixture();
        var settings = Path.Combine(fixture.Project, "settings.json");
        File.WriteAllText(settings, """{"packages":["./project-package"]}""");
        var manager = new CodingAgentPackageManager(fixture.Project, Path.Combine(fixture.Agent, "settings.json"), settings)
            { IsProjectTrusted = false };
        Assert.Empty(manager.ListConfiguredPackages());
        Assert.Empty(manager.ResolveResources().ExtensionPaths);
        Assert.Throws<InvalidOperationException>(() => manager.Install("./project-package", local: true));
        Assert.Throws<InvalidOperationException>(() => manager.Remove("./project-package", local: true));
        Assert.Throws<InvalidOperationException>(() => manager.AddSource("./project-package", local: true));
        Assert.Throws<InvalidOperationException>(() => manager.RemoveSource("./project-package", local: true));
    }

    /// <summary>【CodingAgent】【信任参数】原生 CLI 参数最后出现的决定生效且不进入扩展参数集合。</summary>
    [Fact]
    public void Cli_ParsesTrustOverrides()
    {
        var args = CodingAgentCliArguments.Parse(["--approve", "-na"]);
        Assert.False(args.ProjectTrustOverride);
        Assert.Empty(args.ExtensionFlags);
        Assert.True(CodingAgentCliArguments.Parse(["--no-approve", "-a"]).ProjectTrustOverride);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tau-trust-" + Guid.NewGuid().ToString("N"));
        public string Agent => Path.Combine(Root, "agent");
        public string Project => Path.Combine(Root, "project");
        /// <summary>【CodingAgent】【信任测试】创建隔离的项目和 Agent 目录。</summary>
        public Fixture() { Directory.CreateDirectory(Agent); Directory.CreateDirectory(Project); }
        /// <summary>【CodingAgent】【信任测试】删除已确认的隔离目录。</summary>
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
