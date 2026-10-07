// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【会话生命周期回归】验证默认隔离、继续范围、思考等级优先级及分支恢复。</summary>
[Collection(nameof(CodingAgentSessionTargetEnvironmentCollection))]
public sealed class CodingAgentSessionLifecycleTests
{
    /// <summary>恢复优先级保留显式关闭，与缺失记录区分，并限制模型能力。</summary>
    /// <param name="requested">显式等级。</param>
    /// <param name="persisted">保存等级。</param>
    /// <param name="settings">设置等级。</param>
    /// <param name="reasoning">模型是否支持推理。</param>
    /// <param name="expected">预期等级。</param>
    [Theory]
    [InlineData("off", "high", "high", true, ThinkingLevel.Off)]
    [InlineData("low", "high", "medium", true, ThinkingLevel.Low)]
    [InlineData(null, "off", "high", true, ThinkingLevel.Off)]
    [InlineData(null, "high", "off", true, ThinkingLevel.High)]
    [InlineData(null, null, "off", true, ThinkingLevel.Off)]
    [InlineData(null, null, null, true, ThinkingLevel.Medium)]
    [InlineData(null, "unknown", "low", true, ThinkingLevel.Low)]
    [InlineData(null, "xhigh", null, true, ThinkingLevel.High)]
    [InlineData(null, "high", "high", false, null)]
    public void ThinkingPriority_DistinguishesOffFromMissing(string? requested, string? persisted, string? settings, bool reasoning, ThinkingLevel? expected)
    {
        Assert.Equal(expected, CodingAgentThinkingLevels.ResolveStartup(TestModel with { Reasoning = reasoning }, requested, persisted, settings));
    }

    /// <summary>默认新建使用不同文件和正确 cwd，继续只选最近有内容的会话，另一项目不会继承历史。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Sdk_DefaultSessionsAreIndependentAndContinueIsScoped()
    {
        using var fixture = new Fixture();
        using var first = await fixture.CreateAsync();
        first.Runner.RestoreSession(new CodingAgentSessionSnapshot([new UserMessage("first")], null, null, "first") { ThinkingLevel = "high" });
        first.Save();
        var firstPath = first.TreeSessionController!.Path;
        using var second = await fixture.CreateAsync();
        Assert.NotEqual(firstPath, second.TreeSessionController!.Path);
        Assert.Empty(second.Messages.OfType<UserMessage>());
        Assert.Equal(fixture.Cwd, second.TreeSessionController.GetSessionHeader().Cwd);
        using var resumed = await fixture.CreateAsync(continueSession: true);
        Assert.Equal(firstPath, resumed.TreeSessionController!.Path);
        Assert.Equal("first", resumed.Runner.SessionName);
        Assert.Equal(ThinkingLevel.High, resumed.Runner.ThinkingLevel);
        using var other = await fixture.CreateAsync(continueSession: true, otherCwd: true);
        Assert.Empty(other.Messages.OfType<UserMessage>());
        Assert.NotEqual(Path.GetDirectoryName(firstPath), Path.GetDirectoryName(other.TreeSessionController!.Path));
        Assert.Equal(ThinkingLevel.High, first.Runner.ThinkingLevel);
    }

    /// <summary>SDK 显式恢复 JSON 与 JSONL 时采用同一优先级，旧会话没有等级时沿用设置。</summary>
    /// <param name="tree">是否使用树会话。</param>
    /// <param name="persisted">保存等级。</param>
    /// <param name="requested">SDK 显式等级。</param>
    /// <param name="expected">预期生效等级。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true, "high", null, ThinkingLevel.High)]
    [InlineData(false, "high", null, ThinkingLevel.High)]
    [InlineData(true, "off", null, ThinkingLevel.Off)]
    [InlineData(false, "off", null, ThinkingLevel.Off)]
    [InlineData(true, "high", ThinkingLevel.Off, ThinkingLevel.Off)]
    [InlineData(false, "high", ThinkingLevel.Low, ThinkingLevel.Low)]
    [InlineData(true, null, null, ThinkingLevel.Off)]
    [InlineData(false, null, null, ThinkingLevel.Off)]
    public async Task Sdk_RestoreThinkingFromBothFormats(bool tree, string? persisted, ThinkingLevel? requested, ThinkingLevel expected)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, tree ? "session.jsonl" : "session.json");
        var settingsStore = new CodingAgentSettingsStore(fixture.Settings);
        settingsStore.Save(settingsStore.Load() with { DefaultThinkingLevel = "off" });
        if (tree)
        {
            var store = new CodingAgentTreeSessionStore(path, fixture.Cwd);
            store.AppendModelChange(TestModel.Provider, TestModel.Id);
            if (persisted is not null) store.AppendThinkingLevelChange(persisted);
            store.AppendMessages([new UserMessage("saved")], 0);
        }
        else new CodingAgentSessionStore(path).Save([new UserMessage("saved")], TestModel, "saved", persisted);
        using var session = await fixture.CreateAsync(path, requested: requested);
        Assert.Equal(expected, session.Runner.ThinkingLevel);
        Assert.Single(session.Messages.OfType<UserMessage>());
        session.Save();
        Assert.Equal(CodingAgentThinkingLevels.Format(expected), session.SessionStore!.Load().ThinkingLevel);
        session.Runner.ThinkingLevel = ThinkingLevel.High;
        session.Runner.RestoreSession(new CodingAgentSessionSnapshot([], null, null, null) { ThinkingLevel = "off" });
        Assert.Equal(ThinkingLevel.Off, session.Runner.ThinkingLevel);
    }

    /// <summary>共享目录中其他 cwd 的较新会话不能被 continue 选中。</summary>
    [Fact]
    public void Continue_SharedDirectoryFiltersWorkingDirectory()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, "shared");
        var first = CodingAgentSessionTarget.Resolve(null, sessionDirectory: directory, workingDirectory: fixture.Cwd);
        first.TreeSessionController!.Store.AppendMessages([new UserMessage("correct")], 0);
        var other = CodingAgentSessionTarget.Resolve(null, sessionDirectory: directory, workingDirectory: fixture.OtherCwd);
        other.TreeSessionController!.Store.AppendMessages([new UserMessage("unrelated")], 0);
        File.SetLastWriteTimeUtc(other.TreeSessionController.Path, DateTime.UtcNow.AddMinutes(2));
        var resumed = CodingAgentSessionTarget.Resolve(null, continueRecent: true, sessionDirectory: directory, workingDirectory: fixture.Cwd);
        Assert.Equal(first.TreeSessionController.Path, resumed.TreeSessionController!.Path);
        var fresh = CodingAgentSessionTarget.Resolve(null, workingDirectory: fixture.Cwd, agentDirectory: fixture.Agent);
        var another = CodingAgentSessionTarget.Resolve(null, workingDirectory: fixture.Cwd, agentDirectory: fixture.Agent);
        Assert.NotEqual(fresh.TreeSessionController!.Path, another.TreeSessionController!.Path);
        Assert.Empty(another.LoadInitialSnapshot().Messages);
    }

    /// <summary>显式环境路径继续兼容旧文件，禁用会话优先于环境配置。</summary>
    /// <param name="tree">是否使用 JSONL 环境路径。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnvironmentPath_RemainsExplicitAndNoSessionWins(bool tree)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, tree ? "legacy.jsonl" : "legacy.json");
        if (tree) new CodingAgentTreeSessionStore(path).AppendMessages([new UserMessage("legacy")], 0);
        else new CodingAgentSessionStore(path).Save([new UserMessage("legacy")]);
        Environment.SetEnvironmentVariable(tree ? "TAU_CODING_AGENT_TREE_SESSION_FILE" : "TAU_CODING_AGENT_SESSION_FILE", path);
        Assert.Single(CodingAgentSessionTarget.Resolve(null).LoadInitialSnapshot().Messages);
        Assert.Empty(CodingAgentSessionTarget.Resolve(null, noSession: true).LoadInitialSnapshot().Messages);
    }

    /// <summary>每个分支及其导出保留自身等级，切换回旧节点不会读到其他分支的最新等级。</summary>
    /// <param name="level">待保存等级。</param>
    [Theory]
    [InlineData("off")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    public void Tree_ThinkingFollowsBranchAndExport(string level)
    {
        using var fixture = new Fixture();
        var store = new CodingAgentTreeSessionStore(Path.Combine(fixture.Root, "tree.jsonl"), fixture.Cwd);
        store.AppendThinkingLevelChange(level);
        var branchPoint = Assert.Single(store.AppendMessages([new UserMessage("branch point")], 0));
        store.AppendThinkingLevelChange(level == "off" ? "high" : "off");
        store.AppendMessages([new UserMessage("other branch")], 0);
        store.BranchTo(branchPoint);
        Assert.Equal(level, store.LoadCurrentBranchSnapshot().ThinkingLevel);
        var export = Path.Combine(fixture.Root, "export.jsonl");
        store.ExportCurrentBranch(export);
        Assert.Equal(level, new CodingAgentTreeSessionStore(export).LoadCurrentBranchSnapshot().ToFlatSnapshot().ThinkingLevel);
    }

    /// <summary>同步去重，新会话与替换历史后仍保存等级；旧分支缺字段继续返回 null。</summary>
    [Fact]
    public void Tree_ThinkingChangeIsDeduplicatedAndSurvivesReset()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "tree.jsonl");
        var controller = CodingAgentTreeSessionController.OpenOrCreate(path);
        Assert.Null(controller.LoadSnapshot().ThinkingLevel);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyEvents()) { ThinkingLevel = ThinkingLevel.High };
        runner.MutableMessages.Add(new UserMessage("test"));
        controller.SyncFromRunner(runner);
        controller.SyncFromRunner(runner);
        Assert.Single(File.ReadLines(path), line => line.Contains("thinking_level_change", StringComparison.Ordinal));
        controller.StartNewFromRunner(runner);
        Assert.Equal("high", controller.LoadSnapshot().ThinkingLevel);
        runner.ThinkingLevel = null;
        controller.ReplaceWithRunnerSession(runner);
        Assert.Equal("off", controller.LoadSnapshot().ThinkingLevel);
        var restored = CodingAgentTreeSessionController.OpenOrCreate(path);
        var entries = restored.GetSummary().EntryCount;
        restored.SyncFromRunner(runner);
        Assert.Equal(entries, restored.GetSummary().EntryCount);
    }

    /// <summary>RPC 修改等级即使没有设置文件，也会写入会话以供下次恢复。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Rpc_ThinkingChangesPersistWithoutSettingsStore()
    {
        using var fixture = new Fixture();
        var tree = CodingAgentTreeSessionController.OpenOrCreate(Path.Combine(fixture.Root, "rpc.jsonl"));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyEvents());
        using var input = new StringReader("{\"type\":\"set_thinking_level\",\"level\":\"high\"}\n{\"type\":\"set_thinking_level\",\"level\":\"off\"}\n");
        using var output = new StringWriter();
        Assert.Equal(0, await new CodingAgentRpcHost(runner, input, output, treeSessionController: tree).RunAsync());
        Assert.Equal("off", tree.LoadSnapshot().ThinkingLevel);
        Assert.Equal(2, File.ReadLines(tree.Path).Count(line => line.Contains("thinking_level_change", StringComparison.Ordinal)));
        Assert.DoesNotContain("\"success\":false", output.ToString());
    }

    /// <summary>RPC 仅更新思考设置、不更换模型时也应同步会话文件。</summary>
    /// <param name="tree">是否保存 JSONL，否则保存平面 JSON。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rpc_SettingsUpdatePersistsThinkingWithoutModelChange(bool tree)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, tree ? "rpc.jsonl" : "rpc.json");
        var controller = tree ? CodingAgentTreeSessionController.OpenOrCreate(path) : null;
        var flat = tree ? null : new CodingAgentSessionStore(path);
        var settings = new CodingAgentSettingsStore(fixture.Settings);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyEvents());
        using var input = new StringReader("{\"type\":\"update_settings\",\"settings\":{\"defaultThinkingLevel\":\"off\"}}\n");
        using var output = new StringWriter();
        Assert.Equal(0, await new CodingAgentRpcHost(runner, input, output, sessionStore: flat,
            settingsStore: settings, treeSessionController: controller).RunAsync());
        Assert.Equal("off", settings.Load().DefaultThinkingLevel);
        Assert.Equal("off", flat?.Load().ThinkingLevel ?? controller!.LoadSnapshot().ThinkingLevel);
        Assert.DoesNotContain("\"success\":false", output.ToString());
    }

    /// <summary>交互命令独立于全局设置记录等级，并在摘要分支重建根节点时保留。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Command_ThinkingPersistsAndRootSummaryRetainsIt()
    {
        using var fixture = new Fixture();
        var controller = CodingAgentTreeSessionController.OpenOrCreate(Path.Combine(fixture.Root, "command.jsonl"));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyEvents());
        runner.MutableMessages.Add(new UserMessage("conversation"));
        var result = await new CodingAgentCommandRouter(runner, treeSessionController: controller).TryHandleAsync("/thinking high");
        Assert.False(result.IsError);
        Assert.Equal("high", controller.LoadSnapshot().ThinkingLevel);
        var snapshot = controller.SummarizeCurrentBranchToRoot(runner,
            new CodingAgentBranchSummaryResult("summary", controller.GetSummary().EntryCount));
        Assert.Equal("high", snapshot.ThinkingLevel);
    }

    private static readonly Model TestModel = new()
    {
        Provider = "session-test", Id = "model", Name = "Session Test", Api = "openai-responses", Reasoning = true
    };

    /// <summary>提供无需网络的空事件流。</summary>
    /// <returns>空事件流。</returns>
    private static async IAsyncEnumerable<AgentEvent> EmptyEvents()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>【CodingAgent】【会话夹具】隔离项目目录、Agent 目录及环境指定的会话文件。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string? _flat = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
        private readonly string? _tree = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tau-session-lifecycle-" + Guid.NewGuid().ToString("N"));
        public string Cwd => Path.Combine(Root, "project");
        public string OtherCwd => Path.Combine(Root, "other");
        public string Agent => Path.Combine(Root, "agent");
        public string Settings => Path.Combine(Cwd, ".tau", "coding-agent-settings.json");

        /// <summary>创建专用临时目录并屏蔽全局会话路径。</summary>
        public Fixture()
        {
            Directory.CreateDirectory(Cwd);
            Directory.CreateDirectory(OtherCwd);
            Directory.CreateDirectory(Agent);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE", null);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE", null);
        }

        /// <summary>创建不加载外部资源的 SDK 会话。</summary>
        /// <param name="sessionPath">可选显式路径。</param>
        /// <param name="continueSession">是否继续当前项目会话。</param>
        /// <param name="otherCwd">是否使用另一工作目录。</param>
        /// <param name="requested">显式思考等级。</param>
        /// <returns>已创建会话。</returns>
        public Task<CodingAgentSdkSession> CreateAsync(string? sessionPath = null, bool continueSession = false, bool otherCwd = false, ThinkingLevel? requested = null)
        {
            var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([]));
            catalog.RegisterModel(TestModel);
            return CodingAgentSdk.CreateSessionAsync(new()
            {
                Cwd = otherCwd ? OtherCwd : Cwd, AgentDirectory = Agent,
                SessionPath = sessionPath, ContinueSession = continueSession,
                ProviderId = TestModel.Provider, ModelId = TestModel.Id, ModelCatalog = catalog,
                ThinkingLevel = requested, NoTools = CodingAgentSdkNoToolsMode.All,
                IncludeExtensions = false, IncludeSkills = false, IncludePromptTemplates = false, IncludeContextFiles = false
            });
        }

        /// <summary>恢复环境，并仅清理已校验位置的本夹具目录。</summary>
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE", _flat);
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_TREE_SESSION_FILE", _tree);
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(Path.GetFullPath(Root)) != expectedParent || !Path.GetFileName(Root).StartsWith("tau-session-lifecycle-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test directory.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
