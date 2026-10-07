// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Platform;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Tests;

/// <summary>【AgentCore】【基线回归】验证构造、重置、提示更新与会话恢复的一致性。</summary>
public sealed class AgentBaselineTests
{
    /// <summary>提示或工具非空时创建一条开场声明，空配置保持空会话。</summary>
    /// <param name="prompt">初始提示。</param>
    /// <param name="hasTools">是否提供工具。</param>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("base", false)]
    [InlineData(null, true)]
    [InlineData("base", true)]
    public void Constructor_CreatesOnlyNecessaryBaseline(string? prompt, bool hasTools)
    {
        var agent = Create(new CaptureProvider(), prompt, hasTools ? [new TestTool("first")] : []);
        if (string.IsNullOrEmpty(prompt) && !hasTools) Assert.Empty(agent.State.Messages);
        else
        {
            var initial = Assert.IsType<SystemMessage>(Assert.Single(agent.State.Messages));
            Assert.Equal(prompt ?? "", initial.Content);
            Assert.Equal(DateTimeOffset.UnixEpoch, initial.Timestamp);
            Assert.Equal(hasTools ? ["first"] : Array.Empty<string>(), (initial.ToolsAdded ?? []).Select(tool => tool.Name));
        }
        Assert.Equal(prompt ?? "", agent.State.SystemPrompt);
    }

    /// <summary>历史首条系统声明优先于旧配置，并隔离顶层消息与工具集合。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Constructor_PreservesExistingBaselineAndDeclaresExecutionDifference()
    {
        var original = new SystemMessage("restored") { ToolsAdded = [Definition("old")], Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(123) };
        var messages = new List<ChatMessage> { original, new UserMessage("history") };
        var tools = new List<IAgentTool> { new TestTool("new") };
        var provider = new CaptureProvider();
        var agent = Create(provider, "ignored", tools, messages);
        messages.Clear();
        tools.Clear();
        Assert.Same(original, agent.State.Messages[0]);
        Assert.Equal("restored", agent.SystemPrompt);
        Assert.Single(agent.Tools);
        var events = new List<AgentEvent>();
        agent.Subscribe(events.Add);
        await agent.PromptAsync("next");
        var declaration = Assert.Single(events.OfType<MessageEndEvent>().Select(evt => evt.Message).OfType<SystemMessage>());
        Assert.Equal("old", Assert.Single(declaration.ToolsRemoved!).Name);
        Assert.Equal("new", Assert.Single(declaration.ToolsAdded!).Name);
        Assert.Null(provider.Contexts[0].SystemPrompt);
        Assert.Same(original, provider.Contexts[0].Messages[0]);
    }

    /// <summary>构造生成的工具定义是快照，执行对象变化在下一次请求产生差异。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Constructor_SnapshotsMutableToolDeclaration()
    {
        var variants = new Dictionary<string, string> { ["regex"] = "old" };
        var tool = new TestTool("first") { ConstrainedSampling = new() { Type = "grammar", Variants = variants } };
        var agent = Create(new CaptureProvider(), "base", [tool]);
        var initial = Assert.IsType<SystemMessage>(Assert.Single(agent.State.Messages));
        tool.Description = "changed";
        variants["regex"] = "new";
        Assert.Equal("description", initial.ToolsAdded![0].Description);
        Assert.Equal("old", initial.ToolsAdded[0].ConstrainedSampling!.Variants!["regex"]);
        await agent.PromptAsync("hello");
        var update = agent.State.Messages.OfType<SystemMessage>().Last();
        Assert.Equal("changed", Assert.Single(update.ToolsAdded!).Description);
        Assert.Equal("first", Assert.Single(update.ToolsRemoved!).Name);
    }

    /// <summary>Reset 保留重放后的段落和声明，清空队列与错误；尚未发布的执行工具变化延后声明。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Reset_ReplaysSectionsAndDeclaredToolsWithoutCommittingPendingLoadout()
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(123);
        var provider = new CaptureProvider { Error = true };
        var agent = Create(provider, "ignored", [new TestTool("second")],
        [
            new SystemMessage("base") { Timestamp = timestamp, ToolsAdded = [Definition("first")], Sections = new Dictionary<string, string?> { ["a"] = "old", ["b"] = "deleted" } },
            new UserMessage("history"),
            new SystemMessage("later") { ToolsRemoved = [new("first")], ToolsAdded = [Definition("second")], Sections = new Dictionary<string, string?> { ["a"] = "new", ["b"] = null } }
        ]);
        await agent.PromptAsync("fail");
        Assert.NotNull(agent.State.ErrorMessage);
        agent.Tools = [new TestTool("third")];
        agent.Steer(new UserMessage("discard steering"));
        agent.FollowUp(new UserMessage("discard follow-up"));
        agent.Reset();
        var baseline = Assert.IsType<SystemMessage>(Assert.Single(agent.State.Messages));
        Assert.Equal(timestamp, baseline.Timestamp);
        Assert.Equal("base\n\nlater", baseline.Content);
        Assert.Equal("new", baseline.Sections!["a"]);
        Assert.False(baseline.Sections.ContainsKey("b"));
        Assert.Equal("second", Assert.Single(baseline.ToolsAdded!).Name);
        Assert.Null(baseline.ToolsRemoved);
        Assert.Equal("base\n\nlater\n\nnew", agent.State.SystemPrompt);
        Assert.Null(agent.State.ErrorMessage);
        Assert.False(agent.State.IsStreaming);
        Assert.Empty(agent.State.PendingToolCalls);
        Assert.False(agent.HasQueuedMessages);
        Assert.Equal("third", Assert.Single(agent.Tools).Name);
        provider.Error = false;
        var events = new List<AgentEvent>();
        agent.Subscribe(events.Add);
        await agent.PromptAsync("fresh");
        var change = Assert.Single(events.OfType<MessageEndEvent>().Select(evt => evt.Message).OfType<SystemMessage>());
        Assert.Equal("second", Assert.Single(change.ToolsRemoved!).Name);
        Assert.Equal("third", Assert.Single(change.ToolsAdded!).Name);
    }

    /// <summary>只有系统声明时 Continue 拒绝请求；空会话 Reset 不创建无意义基线。</summary>
    /// <param name="prompt">构造提示。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("base")]
    public async Task Continue_RejectsBaselineOnlyConversation(string? prompt)
    {
        var provider = new CaptureProvider();
        var agent = Create(provider, prompt, []);
        agent.Reset();
        Assert.Equal(prompt is null ? 0 : 1, agent.State.Messages.Count);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ContinueAsync());
        Assert.Contains("No messages", error.Message);
        Assert.Empty(provider.Contexts);
    }

    /// <summary>上下文转换删除系统声明后，旧顶层字段不能偷偷补回提示或工具。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ConvertToLlm_CanRemoveBaselineFromRequestWithoutChangingState()
    {
        var provider = new CaptureProvider();
        var agent = Create(provider, "base", [new TestTool("first")]);
        agent.ConvertToLlm = messages => messages.Where(message => message is not SystemMessage).ToArray();
        await agent.PromptAsync("hello");
        var context = Assert.Single(provider.Contexts);
        Assert.Null(context.SystemPrompt);
        Assert.Null(context.Tools);
        Assert.IsType<UserMessage>(Assert.Single(context.Messages));
        Assert.Equal("base", agent.State.SystemPrompt);
        Assert.Equal("first", Assert.Single(Transcript.GetCurrentTools(agent.State.Messages)).Name);
    }

    /// <summary>兼容提示赋值会替换全部文本和段落，但保留工具声明位置和执行集合。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task SystemPromptSetter_ReplacesTextWithoutDuplicatingBaseline()
    {
        var provider = new CaptureProvider();
        var agent = Create(provider, "base", [new TestTool("first")]);
        await agent.PromptAsync([new SystemMessage("later") { Sections = new Dictionary<string, string?> { ["a"] = "section" } }, new UserMessage("one")]);
        var count = agent.State.Messages.Count;
        agent.SystemPrompt = "replacement";
        Assert.Equal(count, agent.State.Messages.Count);
        Assert.Equal("replacement", agent.State.SystemPrompt);
        Assert.Equal("first", Assert.Single(Transcript.GetCurrentTools(agent.State.Messages)).Name);
        await agent.PromptAsync("two");
        Assert.Equal("replacement", Transcript.GetCurrentSystemPrompt(provider.Contexts[^1].Messages));
        agent.SystemPrompt = null;
        agent.Reset();
        var emptyPrompt = Assert.IsType<SystemMessage>(Assert.Single(agent.State.Messages));
        Assert.Equal("", emptyPrompt.Content);
        Assert.Equal("first", Assert.Single(emptyPrompt.ToolsAdded!).Name);
    }

    /// <summary>门面的请求准备钩子看到重放提示，旧 SystemPrompt 更新接口写回会话。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task PrepareRequest_ReplacesTranscriptPromptAndUpdatesFinishContext()
    {
        var provider = new CaptureProvider();
        var agent = Create(provider, "base", []);
        AgentLoopTurnContext? finished = null;
        agent.PrepareRequestAsync = (request, _) =>
        {
            Assert.Equal("base\n\nlater", request.SystemPrompt);
            return Task.FromResult<AgentRequestUpdate?>(new(SystemPrompt: "updated"));
        };
        agent.FinishTurnAsync = (turn, _) => { finished = turn; return Task.FromResult<AgentTurnDecision?>(null); };
        await agent.PromptAsync([new SystemMessage("later"), new UserMessage("hello")]);
        Assert.Equal("updated", agent.State.SystemPrompt);
        Assert.Equal("updated", finished!.SystemPrompt);
        Assert.Equal("updated", Transcript.GetCurrentSystemPrompt(Assert.Single(provider.Contexts).Messages));
    }

    /// <summary>失败运行的系统更新不能污染应用层回滚后的基线。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Application_RollsBackFailedSystemUpdate()
    {
        var provider = new CaptureProvider { Error = true };
        var registry = Registry(provider);
        var app = AgentApplication.CreateBuilder().UseModel(Model(provider)).UseProviderRegistry(registry).UseSystemPrompt("base").Build();
        var result = await app.PromptAsync([new SystemMessage("failed update"), new UserMessage("fail")]);
        Assert.False(result.IsSuccess);
        Assert.Equal("base", app.State.SystemPrompt);
        Assert.Equal("base", Assert.IsType<SystemMessage>(Assert.Single(app.State.Messages)).Content);
    }

    /// <summary>建立配置可选的代理。</summary>
    /// <param name="provider">记录请求的 provider。</param>
    /// <param name="prompt">初始提示。</param>
    /// <param name="tools">可执行工具。</param>
    /// <param name="messages">恢复历史。</param>
    /// <returns>代理实例。</returns>
    private static Agent Create(CaptureProvider provider, string? prompt, IReadOnlyList<IAgentTool> tools, IReadOnlyList<ChatMessage>? messages = null) =>
        new(new AgentOptions { Model = Model(provider), ProviderRegistry = Registry(provider), SystemPrompt = prompt, Tools = tools, Messages = messages ?? [] });

    /// <summary>创建测试模型。</summary>
    /// <param name="provider">协议实现。</param>
    /// <returns>测试模型。</returns>
    private static Model Model(CaptureProvider provider) => new() { Id = "test", Name = "Test", Api = provider.Api, Provider = "test" };

    /// <summary>创建独立注册表。</summary>
    /// <param name="provider">协议实现。</param>
    /// <returns>只包含该实现的注册表。</returns>
    private static ProviderRegistry Registry(CaptureProvider provider) { var registry = new ProviderRegistry(); registry.Register(provider.Api, () => provider); return registry; }

    /// <summary>创建纯工具声明。</summary>
    /// <param name="name">工具名。</param>
    /// <returns>声明快照。</returns>
    private static Tool Definition(string name) { using var schema = JsonDocument.Parse("{\"type\":\"object\"}"); return new(name, "description", schema.RootElement.Clone()); }

    private sealed class TestTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description { get; set; } = "description";
        public JsonElement ParameterSchema => Definition(name).ParameterSchema;
        public ConstrainedSamplingConfig? ConstrainedSampling { get; init; }
        /// <summary>基线测试不应执行工具。</summary>
        /// <param name="toolCallId">调用标识。</param>
        /// <param name="args">参数。</param>
        /// <param name="ct">取消信号。</param>
        /// <param name="onUpdate">增量回调。</param>
        /// <returns>不会正常返回。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null) => throw new InvalidOperationException("Unexpected tool call");
    }

    private sealed class CaptureProvider : IStreamProvider
    {
        public string Api => "baseline-test";
        public bool SupportsTranscriptContext => true;
        public bool Error { get; set; }
        public List<LlmContext> Contexts { get; } = [];
        /// <summary>保存原生上下文并产生选定终态。</summary>
        /// <param name="model">模型。</param>
        /// <param name="context">请求上下文。</param>
        /// <param name="options">流选项。</param>
        /// <returns>已结束的流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            Contexts.Add(context with { Messages = context.Messages.ToArray() });
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent("done")]) { StopReason = Error ? StopReason.Error : StopReason.EndTurn, ErrorMessage = Error ? "failed" : null }));
            return stream;
        }
        /// <summary>复用普通请求记录逻辑。</summary>
        /// <param name="model">模型。</param>
        /// <param name="context">上下文。</param>
        /// <param name="options">简化选项。</param>
        /// <returns>已结束的流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);
    }
}
