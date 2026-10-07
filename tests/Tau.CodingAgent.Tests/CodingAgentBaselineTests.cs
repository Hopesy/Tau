// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【会话基线回归】覆盖直接入口、压缩、恢复和树存储的完整链路。</summary>
public sealed class CodingAgentBaselineTests
{
    /// <summary>【CodingAgent】【思考等级持久化】平面会话、树会话及分支导出均保留 Anthropic 原生等级。</summary>
    [Fact]
    public void SessionStores_PreserveProviderThinkingLevel()
    {
        using var temp = new TestDirectory();
        ChatMessage[] messages = [new UserMessage("hi"), new AssistantMessage([new TextContent("answer")])
        { Api = "anthropic-messages", Provider = "test", ProviderThinkingLevel = "max" }];
        var flat = new CodingAgentSessionStore(Path.Combine(temp.Path, "flat.json"));
        flat.Save(messages);
        Assert.Equal("max", Assert.Single(new CodingAgentSessionStore(flat.Path).LoadStrict().Messages.OfType<AssistantMessage>()).ProviderThinkingLevel);
        var tree = new CodingAgentTreeSessionStore(Path.Combine(temp.Path, "tree.jsonl"), cwd: temp.Path);
        tree.AppendMessages(messages, 0);
        var reopened = new CodingAgentTreeSessionStore(tree.Path, cwd: temp.Path);
        Assert.Equal("max", Assert.Single(reopened.LoadCurrentBranchSnapshot().Messages.OfType<AssistantMessage>()).ProviderThinkingLevel);
        var branch = new CodingAgentTreeSessionStore(reopened.ExportCurrentBranchToNewSession(), cwd: temp.Path);
        Assert.Equal("max", Assert.Single(branch.LoadCurrentBranchSnapshot().Messages.OfType<AssistantMessage>()).ProviderThinkingLevel);
    }

    /// <summary>四个输入入口都应复用开场声明，生命周期只发布本次新输入输出。</summary>
    /// <param name="inputKind">输入重载编号。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Run_UsesSingleInitialBaselineAcrossInputOverloads(int inputKind)
    {
        var provider = new CaptureProvider();
        var runner = CreateRunner(provider, tools: [new TestTool("read")]);
        var initial = Assert.IsType<SystemMessage>(Assert.Single(runner.Messages));
        Assert.Equal("base", initial.Content);
        Assert.Equal("read", Assert.Single(initial.ToolsAdded!).Name);
        Assert.Equal(DateTimeOffset.UnixEpoch, initial.Timestamp);
        var events = new List<AgentEvent>();
        var stream = inputKind switch
        {
            0 => runner.RunAsync("hello"),
            1 => runner.RunAsync((IReadOnlyList<ContentBlock>)[new TextContent("hello")]),
            2 => runner.RunAsync((ChatMessage)new UserMessage("hello")),
            _ => runner.RunAsync((IReadOnlyList<ChatMessage>)[new UserMessage("hello")])
        };
        await foreach (var value in stream) events.Add(value);
        var context = Assert.Single(provider.Contexts);
        Assert.Null(context.SystemPrompt);
        Assert.Same(initial, Assert.Single(context.Messages.OfType<SystemMessage>()));
        Assert.Equal(["user", "assistant"], Assert.Single(events.OfType<AgentEndEvent>()).Messages.Select(message => message.Role));
        Assert.DoesNotContain(events.OfType<MessageStartEvent>(), value => value.Message is SystemMessage);
    }

    /// <summary>恢复的声明优先于构造配置，重置保留重放状态并清空队列。</summary>
    [Fact]
    public void RestoreAndReset_PreserveDeclaredSectionsAndTools()
    {
        var runner = CreateRunner(new CaptureProvider(), tools: [new TestTool("executable")]);
        ChatMessage[] history = [new SystemMessage("restored")
        {
            Sections = new Dictionary<string, string?> { ["mode"] = "old", ["drop"] = "gone" },
            ToolsAdded = [Definition("old")]
        }, new UserMessage("hi"), new SystemMessage("delta")
        {
            Sections = new Dictionary<string, string?> { ["mode"] = "new", ["drop"] = null },
            ToolsRemoved = [new("old")], ToolsAdded = [Definition("declared")]
        }];
        runner.RestoreSession(new(history, null, null, "restored session"));
        runner.Steer("queued");
        runner.FollowUp("later");
        runner.ResetSession();
        var system = Assert.IsType<SystemMessage>(Assert.Single(runner.Messages));
        Assert.Equal("restored\n\ndelta", system.Content);
        Assert.Equal("new", Assert.Single(system.Sections!).Value);
        Assert.Equal("declared", Assert.Single(system.ToolsAdded!).Name);
        Assert.Equal(0, runner.PendingMessageCount);
        Assert.Null(runner.SessionName);
        Assert.Equal(3, history.Length);
        Assert.Equal("old", Assert.IsType<SystemMessage>(history[0]).Sections!["mode"]);
    }

    /// <summary>空配置不产生无意义开场；补入旧会话时不继承上一分支的系统更新。</summary>
    [Fact]
    public void RestoreLegacy_UsesConfiguredBaselineInsteadOfPreviousBranch()
    {
        var empty = CreateRunner(new CaptureProvider(), prompt: null);
        Assert.Empty(empty.Messages);
        empty.ResetSession();
        Assert.Empty(empty.Messages);
        var runner = CreateRunner(new CaptureProvider());
        runner.AppendMessage(new SystemMessage("previous-branch"));
        ChatMessage[] old = [new UserMessage("legacy")];
        runner.RestoreSession(new(old, null, null, null));
        Assert.Equal("base", Transcript.GetCurrentSystemPrompt(runner.Messages));
        Assert.Same(old[0], runner.Messages[1]);
        Assert.Single(old);
    }

    /// <summary>请求准备替换提示后，原生请求和运行器历史应保持一致。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task PrepareRequest_UpdatesTranscriptAndConvertCanRemoveBaseline()
    {
        var provider = new CaptureProvider();
        var runner = CreateRunner(provider, configure: config => config with
        {
            PrepareRequestAsync = (context, _) =>
            {
                Assert.Equal("base", context.SystemPrompt);
                return Task.FromResult<AgentRequestUpdate?>(new(SystemPrompt: "replacement"));
            },
            ConvertToLlm = messages => messages.Where(message => message is not SystemMessage).ToArray()
        });
        await foreach (var _ in runner.RunAsync("hello")) { }
        Assert.Equal("replacement", Transcript.GetCurrentSystemPrompt(runner.Messages));
        var context = Assert.Single(provider.Contexts);
        Assert.Null(context.SystemPrompt);
        Assert.DoesNotContain(context.Messages, message => message is SystemMessage);
    }

    /// <summary>连续压缩保持声明，第二次使用更新摘要模板且不重复加入上次摘要。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Compact_PreservesBaselineAndUpdatesPreviousSummary()
    {
        var provider = new CaptureProvider();
        var runner = CreateRunner(provider, tools: [new TestTool("read")]);
        runner.AppendMessage(new UserMessage("first"));
        runner.AppendMessage(new AssistantMessage([new TextContent("answer")]));
        runner.AppendMessage(new SystemMessage("delta") { Sections = new Dictionary<string, string?> { ["mode"] = "careful" } });
        var first = await runner.CompactAsync();
        Assert.Equal(4, first.MessagesBefore);
        Assert.Equal(2, first.MessagesAfter);
        Assert.Equal("base\n\ndelta\n\ncareful", Transcript.GetCurrentSystemPrompt(runner.Messages));
        Assert.Equal("read", Assert.Single(Transcript.GetCurrentTools(runner.Messages)).Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompactAsync());
        Assert.Single(provider.Contexts);
        runner.AppendMessage(new UserMessage("new work"));
        await runner.CompactAsync();
        Assert.Equal(2, runner.Messages.Count);
        var summaryPrompt = string.Join("\n", provider.Contexts[1].Messages.OfType<UserMessage>().SelectMany(message => message.Content.OfType<TextContent>()).Select(text => text.Text));
        Assert.Contains("summary-1", summaryPrompt, StringComparison.Ordinal);
        Assert.Contains("new work", summaryPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(CodingAgentCompactionMessages.SummaryPrefix, summaryPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("careful", summaryPrompt, StringComparison.Ordinal);
    }

    /// <summary>空、仅基线或单条对话不应发起压缩请求。</summary>
    /// <param name="withInput">是否含一条用户输入。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compact_RejectsInsufficientConversation(bool withInput)
    {
        var provider = new CaptureProvider();
        var runner = CreateRunner(provider);
        if (withInput) runner.AppendMessage(new UserMessage("one"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompactAsync());
        Assert.Empty(provider.Contexts);
        Assert.False(runner.IsCompacting);
    }

    /// <summary>生成失败或完成时取消都不能替换对话或清除待执行队列。</summary>
    /// <param name="cancel">是否在摘要返回时取消。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compact_FailurePreservesHistoryAndQueue(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new CaptureProvider { Empty = !cancel, OnRequest = cancel ? cancellation.Cancel : null };
        var runner = CreateRunner(provider);
        runner.AppendMessage(new UserMessage("hello"));
        runner.AppendMessage(new AssistantMessage([new TextContent("answer")]));
        runner.Steer("keep queued");
        var original = runner.Messages.ToArray();
        await Assert.ThrowsAnyAsync<Exception>(() => runner.CompactAsync(cancellationToken: cancellation.Token));
        Assert.Equal(original, runner.Messages);
        Assert.Equal(1, runner.PendingMessageCount);
        Assert.False(runner.IsCompacting);
    }

    /// <summary>树存储保存压缩基线，重新打开与克隆后仍能应用后续声明并切回旧分支。</summary>
    [Fact]
    public void TreeStore_CompactionReopenCloneAndBranchPreserveBaseline()
    {
        using var temp = new TestDirectory();
        var store = new CodingAgentTreeSessionStore(Path.Combine(temp.Path, "session.jsonl"), cwd: temp.Path);
        var ids = store.AppendMessages([new SystemMessage("base") { ToolsAdded = [Definition("old")] },
            new UserMessage("kept"), new SystemMessage("delta") { ToolsRemoved = [new("old")], ToolsAdded = [Definition("new")] }], 0);
        store.AppendCompaction("summary", ids[1], 30);
        store.AppendMessages([new SystemMessage("after") { Sections = new Dictionary<string, string?> { ["mode"] = "yes" } }], 0);
        store = new CodingAgentTreeSessionStore(store.Path, cwd: temp.Path);
        var snapshot = store.LoadCurrentBranchSnapshot();
        Assert.Equal(["system", "user", "user", "system"], snapshot.Messages.Select(message => message.Role));
        Assert.Equal("base\n\ndelta\n\nafter\n\nyes", Transcript.GetCurrentSystemPrompt(snapshot.Messages));
        Assert.Equal("new", Assert.Single(Transcript.GetCurrentTools(snapshot.Messages)).Name);
        var clonePath = store.ExportCurrentBranchToNewSession();
        var clone = new CodingAgentTreeSessionStore(clonePath, cwd: temp.Path);
        Assert.Equal(Transcript.GetCurrentSystemPrompt(snapshot.Messages), Transcript.GetCurrentSystemPrompt(clone.LoadCurrentBranchSnapshot().Messages));
        clone.AppendCompaction("second", null, 10);
        Assert.Equal(2, clone.LoadCurrentBranchSnapshot().Messages.Count);
        Assert.Equal("new", Assert.Single(Transcript.GetCurrentTools(clone.LoadCurrentBranchSnapshot().Messages)).Name);
        store.BranchWithSummary(ids[0], "other branch");
        Assert.Equal("base", Transcript.GetCurrentSystemPrompt(store.LoadCurrentBranchSnapshot().Messages));
        Assert.Equal("old", Assert.Single(Transcript.GetCurrentTools(store.LoadCurrentBranchSnapshot().Messages)).Name);
    }

    /// <summary>旧树会话补基线后连续同步不重复旧尾部，压缩时把运行器声明写入树记录。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Controller_LegacyRestoreDoesNotDuplicateTailAndCompactionPersistsBaseline()
    {
        using var temp = new TestDirectory();
        var store = new CodingAgentTreeSessionStore(Path.Combine(temp.Path, "legacy.jsonl"), cwd: temp.Path);
        store.AppendMessages([new UserMessage("old"), new AssistantMessage([new TextContent("old answer")])], 0);
        var controller = new CodingAgentTreeSessionController(store, new(0, 0));
        var runner = CreateRunner(new CaptureProvider());
        runner.RestoreSession(controller.LoadSnapshot().ToFlatSnapshot());
        controller.SyncFromRunner(runner);
        Assert.Equal(2, store.LoadCurrentBranchSnapshot().Messages.Count);
        await foreach (var _ in runner.RunAsync("new")) { }
        controller.SyncFromRunner(runner);
        controller.SyncFromRunner(runner);
        Assert.Equal(["user", "assistant", "user", "assistant"], store.LoadCurrentBranchSnapshot().Messages.Select(message => message.Role));
        var result = await runner.CompactAsync();
        controller.RecordCompaction(runner, result);
        Assert.Equal("base", Transcript.GetCurrentSystemPrompt(store.LoadCurrentBranchSnapshot().Messages));
        Assert.Equal("base", Transcript.GetCurrentSystemPrompt(runner.Messages));
        var count = store.LoadCurrentBranchSnapshot().Messages.Count;
        controller.SyncFromRunner(runner);
        Assert.Equal(count, store.LoadCurrentBranchSnapshot().Messages.Count);
    }

    /// <summary>系统 token 估算只计有效分节与工具，移除后恢复为正文成本。</summary>
    [Fact]
    public void TokenEstimate_ReplaysSystemChanges()
    {
        ChatMessage[] messages = [new SystemMessage("base") { ToolsAdded = [Definition("read")], Sections = new Dictionary<string, string?> { ["mode"] = "original" } },
            new SystemMessage("") { ToolsRemoved = [new("read")], Sections = new Dictionary<string, string?> { ["mode"] = "12345678" } }];
        Assert.Equal(3, CodingAgentTokenEstimator.Estimate(messages));
        Assert.True(CodingAgentTokenEstimator.Estimate([messages[0]]) > 3);
    }

    /// <summary>系统更新不占用尾部消息额度，也不作为分支摘要内容。</summary>
    [Fact]
    public void TreeStore_RetentionAndBranchSummaryIgnoreSystemMessages()
    {
        using var temp = new TestDirectory();
        var store = new CodingAgentTreeSessionStore(Path.Combine(temp.Path, "tail.jsonl"), cwd: temp.Path);
        var ids = store.AppendMessages([new SystemMessage("base"), new UserMessage("old"), new UserMessage("kept"),
            new AssistantMessage([new TextContent("answer")]), new SystemMessage("update")], 0);
        Assert.Equal(ids[2], store.FindCompactionFirstKeptEntryId(0, 2));
        var summaryMessages = store.CollectBranchSummaryMessages(null);
        Assert.Equal(3, summaryMessages.Count);
        Assert.DoesNotContain(summaryMessages, message => message is SystemMessage);
    }

    /// <summary>纯系统历史没有可供分支摘要总结的对话。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task BranchSummary_RejectsOnlySystemHistory()
    {
        var provider = new CaptureProvider();
        var runner = CreateRunner(provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.SummarizeBranchAsync(runner.Messages));
        Assert.Empty(provider.Contexts);
    }

    /// <summary>构造使用原生上下文捕获器的运行器。</summary>
    /// <param name="provider">本地提供方。</param>
    /// <param name="prompt">构造提示。</param>
    /// <param name="tools">执行工具。</param>
    /// <param name="configure">可选钩子配置。</param>
    /// <returns>独立运行器。</returns>
    private static RuntimeCodingAgentRunner CreateRunner(CaptureProvider provider, string? prompt = "base", IReadOnlyList<IAgentTool>? tools = null, Func<AgentLoopConfig, AgentLoopConfig>? configure = null)
    {
        var model = new Model { Id = "baseline", Provider = "test", Api = provider.Api, Name = "Baseline" };
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var catalog = new ModelCatalog();
        catalog.RegisterModel(model);
        var config = new AgentLoopConfig { Model = model, ProviderRegistry = registry, Tools = tools ?? [], SystemPrompt = prompt, StreamOptions = new() { ApiKey = "synthetic" } };
        return new(new AgentRuntime(), configure?.Invoke(config) ?? config, catalog);
    }

    /// <summary>创建不依赖 JSON 文档生命周期的声明。</summary>
    /// <param name="name">工具名。</param>
    /// <returns>工具声明。</returns>
    private static Tool Definition(string name)
    {
        using var json = JsonDocument.Parse("{\"type\":\"object\"}");
        return new(name, "tool", json.RootElement.Clone());
    }

    private sealed class TestTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => "tool";
        public JsonElement ParameterSchema => Definition(name).ParameterSchema;
        /// <summary>测试仅声明工具，不应执行。</summary>
        /// <param name="toolCallId">调用标识。</param>
        /// <param name="args">参数。</param>
        /// <param name="ct">取消信号。</param>
        /// <param name="onUpdate">进度回调。</param>
        /// <returns>不正常返回。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null) => throw new InvalidOperationException("Unexpected execution");
    }

    private sealed class CaptureProvider : IStreamProvider
    {
        public string Api => "coding-baseline";
        public bool SupportsTranscriptContext => true;
        public bool Empty { get; init; }
        public Action? OnRequest { get; init; }
        public List<LlmContext> Contexts { get; } = [];
        /// <summary>保存请求快照，返回摘要或空文本。</summary>
        /// <param name="model">模型。</param>
        /// <param name="context">请求。</param>
        /// <param name="options">选项。</param>
        /// <returns>已结束的流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            Contexts.Add(context with { Messages = context.Messages.ToArray() });
            OnRequest?.Invoke();
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent(Empty ? "" : $"summary-{Contexts.Count}")])));
            return stream;
        }
        /// <summary>复用请求记录逻辑。</summary>
        /// <param name="model">模型。</param>
        /// <param name="context">上下文。</param>
        /// <param name="options">选项。</param>
        /// <returns>已结束的流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-coding-baseline-" + Guid.NewGuid().ToString("N"));
        /// <summary>创建独立测试目录。</summary>
        public TestDirectory() => Directory.CreateDirectory(Path);
        /// <summary>清理本测试创建的目录。</summary>
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
