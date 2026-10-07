// 作者：xxx
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentCompositionTurnInputTests
{
    /// <summary>【CodingAgent】【生成中会话替换】新建、恢复和分叉等待旧提供方取消，原会话保存中止结果，新会话不混入旧消息。</summary>
    /// <param name="action">会话快捷键名称。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("new")]
    [InlineData("resume")]
    [InlineData("fork")]
    public async Task StreamingSessionReplacement_SettlesOldTurnBeforeAdoptingSession(string action)
    {
        using var fixture = new StreamingSessionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var provider = new InterruptProvider(); var runner = fixture.CreateRunner(provider);
        var original = fixture.CreateStore("original"); var tree = new CodingAgentTreeSessionController(original);
        var target = fixture.CreateStore("target"); target.AppendModelChange("test", "model"); target.AppendMessages([new UserMessage("target history")], 0);
        var stream = new Events { Gate = provider.Started.Task }; stream.Text("discarded draft"); stream.Key(ConsoleKey.F2);
        var main = new Events(); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)));
        var terminal = new FakeTerminal(); var ui = new InteractiveConsoleSession(terminal, editor);
        var bindings = Native("{\"app.session." + action + "\":\"f2\"}");
        string? selectedUser = null;
        var host = new CodingAgentHost(ui, runner, settingsStore: fixture.Settings, treeSessionController: tree,
            initialMessages: ["start"], turnInputSource: Create(stream, bindings),
            treeNavigator: (_, _, _) => Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(selectedUser, 0, 1)),
            resumeSelector: (_, _) => Task.FromResult(new CodingAgentResumeSelectionResult(target.Path)));
        runner.AppendMessage(new UserMessage("fork source")); runner.AppendMessage(new AssistantMessage([new TextContent("old answer")]));
        tree.SyncFromRunner(runner); selectedUser = tree.GetUserMessagesForForking().First().EntryId;
        var previousId = runner.SessionId;
        await host.RunAsync(deadline.Token);
        Assert.True(provider.Cancelled); Assert.False(runner.IsStreaming); Assert.NotEqual(previousId, runner.SessionId);
        Assert.NotEqual(original.Path, tree.Path); Assert.Equal(1, provider.Calls);
        var savedOriginal = original.LoadCurrentBranchSnapshot();
        Assert.Contains(savedOriginal.Messages.OfType<AssistantMessage>(), message => message.StopReason == StopReason.Aborted);
        Assert.Contains(savedOriginal.Messages.OfType<UserMessage>(), message => message.Content.OfType<TextContent>().Any(text => text.Text == "start"));
        var texts = runner.Messages.OfType<UserMessage>().SelectMany(message => message.Content.OfType<TextContent>()).Select(text => text.Text).ToArray();
        Assert.Equal(action == "resume" ? new[] { "target history" } : [], texts);
        Assert.Equal(action == "fork" ? "fork source" : string.Empty, ui.GetDraft());
        Assert.DoesNotContain("turn input listener failed", terminal.FlattenedText()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【选择取消】取消恢复、分叉或树选择保持当前生成及草稿，随后 Escape 仍可正常中断。</summary>
    /// <param name="action">选择器动作。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("resume")]
    [InlineData("fork")]
    [InlineData("tree")]
    public async Task CancelledSessionSelector_PreservesCurrentSessionAndDraft(string action)
    {
        using var fixture = new StreamingSessionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var provider = new InterruptProvider(); var runner = fixture.CreateRunner(provider);
        var original = fixture.CreateStore("original"); var tree = new CodingAgentTreeSessionController(original);
        var stream = new Events { Gate = provider.Started.Task }; stream.Text("kept draft"); stream.Key(ConsoleKey.F2); stream.Key(ConsoleKey.Escape);
        var main = new Events(); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)));
        var ui = new InteractiveConsoleSession(new FakeTerminal(), editor); var called = false;
        var bindings = Native("{\"app.session." + action + "\":\"f2\"}");
        var host = new CodingAgentHost(ui, runner, settingsStore: fixture.Settings, treeSessionController: tree,
            initialMessages: ["start"], turnInputSource: Create(stream, bindings),
            treeNavigator: (_, _, _) => { called = true; Assert.True(runner.IsStreaming); Assert.False(provider.Cancelled); return Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(null, 0, 1)); },
            resumeSelector: (_, _) => { called = true; Assert.True(runner.IsStreaming); Assert.False(provider.Cancelled); return Task.FromResult(new CodingAgentResumeSelectionResult(null)); });
        runner.AppendMessage(new UserMessage("old input")); tree.SyncFromRunner(runner); var previousId = runner.SessionId;
        await host.RunAsync(deadline.Token);
        Assert.True(called); Assert.True(provider.Cancelled); Assert.Equal(previousId, runner.SessionId);
        Assert.Equal(original.Path, tree.Path); Assert.Equal("kept draft", ui.GetDraft()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【活动分支导航】确认旧节点后中止当前回答并回退分支，待发送输入与草稿恢复到编辑器。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task StreamingTreeNavigation_AbortsThenNavigatesAndRestoresQueuedDraft()
    {
        using var fixture = new StreamingSessionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var provider = new InterruptProvider(); var runner = fixture.CreateRunner(provider);
        var tree = new CodingAgentTreeSessionController(fixture.CreateStore("original"));
        var stream = new Events { Gate = provider.Started.Task }; stream.Text("queued"); stream.Key(ConsoleKey.Enter); stream.Text("draft"); stream.Key(ConsoleKey.F2);
        var main = new Events(); main.Key(ConsoleKey.C, ConsoleModifiers.Control);
        var editor = new InteractiveInputEditor(main, new TuiCompositionInteractiveRenderer(new(new Surface(), main)));
        var terminal = new FakeTerminal(); var ui = new InteractiveConsoleSession(terminal, editor);
        var host = new CodingAgentHost(ui, runner, settingsStore: fixture.Settings, treeSessionController: tree,
            initialMessages: ["start"], turnInputSource: Create(stream, Native("""{"app.session.tree":"f2"}""")),
            treeNavigator: (items, _, _) => Task.FromResult(new CodingAgentTreeInteractiveNavigator.Result(items.First(item => item.MessageRole == "user").EntryId, 0, 1)));
        runner.AppendMessage(new UserMessage("old input")); runner.AppendMessage(new AssistantMessage([new TextContent("old answer")])); tree.SyncFromRunner(runner);
        await host.RunAsync(deadline.Token);
        Assert.True(provider.Cancelled); Assert.Contains("Navigated to selected point", terminal.FlattenedText());
        Assert.Empty(runner.Messages.OfType<UserMessage>()); Assert.Equal(0, runner.PendingMessageCount);
        Assert.Equal("queued\n\ndraft", ui.GetDraft()); Assert.False(deadline.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【会话夹具】创建隔离的 JSONL 存储和只等待取消的本地模型。</summary>
    private sealed class StreamingSessionFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tau-stream-session-" + Guid.NewGuid().ToString("N"));
        public CodingAgentSettingsStore Settings { get; }
        /// <summary>【CodingAgent】【隔离设置】关闭摘要选择提示，使测试聚焦于会话快捷键。</summary>
        public StreamingSessionFixture()
        {
            Directory.CreateDirectory(_root); Settings = new(Path.Combine(_root, "settings.json"));
            using var branch = JsonDocument.Parse("""{"skipPrompt":true}""");
            Settings.Save(new(null, null) { BranchSummary = branch.RootElement.Clone() });
        }
        /// <summary>【CodingAgent】【模型夹具】创建支持真实中断传播的运行器。</summary>
        /// <param name="provider">受控提供方。</param><returns>原生运行器。</returns>
        public RuntimeCodingAgentRunner CreateRunner(InterruptProvider provider)
        {
            var registry = new ProviderRegistry(); registry.Register(provider.Api, () => provider);
            var catalog = new ModelCatalog(); catalog.RegisterModel(new Model { Provider = "test", Id = "model", Name = "Test", Api = provider.Api });
            return RuntimeCodingAgentRunner.Create("test", "model", toolsOverride: [], providerRegistryOverride: registry,
                modelCatalogOverride: catalog, apiKey: "synthetic", workingDirectory: _root);
        }
        /// <summary>【CodingAgent】【会话存储】创建相同工作目录的独立原生会话文件。</summary>
        /// <param name="name">夹具内文件名。</param><returns>会话存储。</returns>
        public CodingAgentTreeSessionStore CreateStore(string name) => new(Path.Combine(_root, name + ".jsonl"), _root);
        /// <summary>【CodingAgent】【夹具清理】释放本夹具唯一临时目录。</summary>
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
