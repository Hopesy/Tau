// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Providers.PiMessages;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.AgentCore.Tests;

/// <summary>【AgentCore】【会话契约回归】覆盖工具声明重放、事件增量和会话恢复。</summary>
public sealed class AgentTranscriptTests
{
    /// <summary>【AgentCore】【Anthropic 会话】跨轮工具替换保留开场定义，并在新用户输入之后发送原生增删。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Prompt_AnthropicToolChangesPreserveCachedPrefix()
    {
        using var handler = new TranscriptHttpHandler { Anthropic = true };
        using var client = new HttpClient(handler);
        var provider = new Tau.Ai.Providers.Anthropic.AnthropicProvider(client);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var first = new TestTool("first");
        var agent = new Agent(new AgentOptions
        {
            Model = new() { Id = "test", Name = "Test", Api = provider.Api, Provider = "test", BaseUrl = "https://example.invalid",
                Compat = new() { SupportsMidConvoSystemMessages = true, SupportsMidConvoToolChanges = true } },
            ProviderRegistry = registry, SystemPrompt = "base", Tools = [first], StreamOptions = new() { ApiKey = "test-key", CacheRetention = CacheRetention.Short }
        });
        await agent.PromptAsync("before");
        agent.Tools = [new TestTool("second")];
        await agent.PromptAsync("after");

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0].GetProperty("tools").GetRawText(), handler.Bodies[1].GetProperty("tools").GetRawText());
        var messages = handler.Bodies[1].GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(new[] { "user", "assistant", "user", "system" }, messages.Select(message => message.GetProperty("role").GetString()));
        var changes = messages[^1].GetProperty("content");
        Assert.Equal("tool_removal", changes[0].GetProperty("type").GetString());
        Assert.Equal("first", changes[0].GetProperty("tool").GetProperty("name").GetString());
        Assert.Equal("tool_addition", changes[1].GetProperty("type").GetString());
        Assert.Equal("second", changes[1].GetProperty("tool").GetProperty("definition").GetProperty("name").GetString());
        Assert.Equal("second", Assert.Single(Transcript.GetCurrentTools(agent.State.Messages)).Name);
        agent.Reset();
        await agent.PromptAsync("reset prompt");
        Assert.Equal("second", handler.Bodies[2].GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.DoesNotContain(handler.Bodies[2].GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "system");
    }

    /// <summary>【AgentCore】【原生会话】真实 Provider 报文保留跨轮工具新增和中途系统指令位置。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Prompt_PreservesTranscriptThroughNativeProviderHttpRequest()
    {
        using var handler = new TranscriptHttpHandler();
        using var client = new HttpClient(handler);
        var provider = new Tau.Ai.Providers.OpenAi.OpenAiProvider(client);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var first = new TestTool("first");
        var agent = new Agent(new AgentOptions
        {
            Model = new() { Id = "test", Name = "Test", Api = provider.Api, Provider = "test", BaseUrl = "https://example.invalid/v1", Reasoning = true,
                Compat = new() { SupportsMidConvoSystemMessages = true, SupportsMidConvoToolAdditions = true, SupportsDeveloperRole = true } },
            ProviderRegistry = registry, SystemPrompt = "base", Tools = [first], StreamOptions = new() { ApiKey = "test-key" }
        });
        await agent.PromptAsync("before");
        agent.Tools = [first, new TestTool("second")];
        await agent.PromptAsync([new SystemMessage("later"), new UserMessage("after")]);

        Assert.Equal(2, handler.Bodies.Count);
        var body = handler.Bodies[1];
        var messages = body.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("base", messages[0].GetProperty("content").GetString());
        var additions = messages.Where(message => message.TryGetProperty("tools", out _)).ToArray();
        Assert.Equal("first", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("second", Assert.Single(additions).GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        var addedIndex = Array.FindIndex(messages, message => message.TryGetProperty("tools", out var tools) && tools[0].GetProperty("function").GetProperty("name").GetString() == "second");
        Assert.Equal("assistant", messages[addedIndex - 1].GetProperty("role").GetString());
        Assert.Equal("later", messages[addedIndex + 1].GetProperty("content").GetString());
        Assert.Equal("after", messages[addedIndex + 2].GetProperty("content").GetString());
        Assert.Equal(new[] { "first", "second" }, Transcript.GetCurrentTools(agent.State.Messages).Select(tool => tool.Name));
    }

    /// <summary>连续提示只声明变化，移除后不重复声明，结束事件不包含先前运行。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Prompt_DeclaresLoadoutChangesAndReturnsOnlyRunMessages()
    {
        var provider = new Provider();
        var first = new TestTool("first");
        var agent = CreateAgent(provider, [first]);
        var ends = new List<AgentEndEvent>();
        agent.Subscribe(evt => { if (evt is AgentEndEvent end) ends.Add(end); });
        await agent.PromptAsync("one");
        agent.Tools = [new TestTool("second")];
        await agent.PromptAsync("two");
        await agent.PromptAsync("three");
        agent.Tools = [];
        await agent.PromptAsync("four");

        Assert.Equal(new[] { 2, 3, 2, 3 }, ends.Select(end => end.Messages.Count));
        var initial = Assert.IsType<SystemMessage>(agent.State.Messages[0]);
        Assert.DoesNotContain(ends[0].Messages, message => message is SystemMessage);
        Assert.Equal("first", Assert.Single(initial.ToolsAdded!).Name);
        var changed = Assert.IsType<SystemMessage>(ends[1].Messages[0]);
        Assert.Equal("first", Assert.Single(changed.ToolsRemoved!).Name);
        Assert.Equal("second", Assert.Single(changed.ToolsAdded!).Name);
        Assert.Null(Assert.IsType<SystemMessage>(ends[3].Messages[0]).ToolsAdded);
        Assert.Equal("second", Assert.Single(Assert.IsType<SystemMessage>(ends[3].Messages[0]).ToolsRemoved!).Name);
        Assert.Equal(new[] { "first", "second", "second", "" }, provider.Contexts.Select(context => string.Join(",", context.Tools!.Select(tool => tool.Name))));
        Assert.All(provider.Contexts, context => Assert.Equal("base prompt", context.SystemPrompt));
        Assert.Equal(11, agent.State.Messages.Count);
        Assert.Empty(Transcript.GetCurrentTools(agent.State.Messages));
        Assert.DoesNotContain(ends[0].Messages[1], ends[1].Messages);
    }

    /// <summary>同名工具接口变化需要移除后新增，历史定义保持不可变快照。</summary>
    /// <param name="field">修改的接口字段。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("description")]
    [InlineData("schema")]
    [InlineData("sampling")]
    public async Task Prompt_RedeclaresChangedInterface(string field)
    {
        var provider = new Provider();
        var tool = new TestTool("tool");
        var agent = CreateAgent(provider, [tool]);
        await agent.PromptAsync("one");
        var original = Assert.Single(Assert.IsType<SystemMessage>(agent.State.Messages[0]).ToolsAdded!);
        if (field == "description") tool.Description = "changed";
        if (field == "schema") tool.ParameterSchema = Json("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"}}}");
        if (field == "sampling") tool.ConstrainedSampling = new() { Type = "json_schema", Strict = "always" };
        await agent.PromptAsync("two");
        var update = agent.State.Messages.OfType<SystemMessage>().Last();
        Assert.Equal("tool", Assert.Single(update.ToolsRemoved!).Name);
        Assert.False(Transcript.DeclarationsEqual(original, Assert.Single(update.ToolsAdded!)));
        Assert.Equal("tool description", original.Description);
        Assert.Null(original.ConstrainedSampling);
        Assert.Equal("{\"type\":\"object\"}", original.ParameterSchema.GetRawText());
    }

    /// <summary>合并待发布系统消息时，保留内容和段落，纠正与执行集合不一致的声明。</summary>
    /// <param name="changeLoadout">是否同时更新真实工具集合。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prompt_RewritesPendingToolIntentWithoutMutatingCaller(bool changeLoadout)
    {
        var provider = new Provider();
        var agent = CreateAgent(provider, [new TestTool("first")]);
        await agent.PromptAsync("one");
        if (changeLoadout) agent.Tools = [new TestTool("second")];
        var pending = new SystemMessage("instructions")
        {
            Timestamp = DateTimeOffset.UnixEpoch, Sections = new Dictionary<string, string?> { ["note"] = "section" },
            ToolsAdded = [new Tool("fake", "fake", Json("{}"))], ToolsRemoved = [new("first")]
        };
        var messages = new List<ChatMessage>();
        using var subscription = agent.Subscribe(evt => { if (evt is MessageEndEvent end) messages.Add(end.Message); });
        await agent.PromptAsync([pending, new UserMessage("two")]);
        var actual = Assert.IsType<SystemMessage>(messages[0]);
        Assert.Equal(pending.Timestamp, actual.Timestamp);
        Assert.Same(pending.Sections, actual.Sections);
        Assert.Equal("instructions", actual.Content);
        Assert.Equal("fake", Assert.Single(pending.ToolsAdded!).Name);
        if (changeLoadout)
        {
            Assert.Equal("second", Assert.Single(actual.ToolsAdded!).Name);
            Assert.Equal("first", Assert.Single(actual.ToolsRemoved!).Name);
        }
        else { Assert.Null(actual.ToolsAdded); Assert.Null(actual.ToolsRemoved); }
        Assert.Equal("base prompt\n\ninstructions\n\nsection", provider.Contexts.Last().SystemPrompt);
        Assert.Equal(changeLoadout ? "second" : "first", Assert.Single(provider.Contexts.Last().Tools!).Name);
    }

    /// <summary>下一回合准备时变更工具，与已选择的队列输入一起发布，且不出现重复声明。</summary>
    /// <param name="followUp">是否使用 follow-up 队列。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareNextTurn_DeclaresBeforeQueuedInput(bool followUp)
    {
        var provider = new Provider();
        var agent = CreateAgent(provider, [new TestTool("first")]);
        var finished = 0;
        agent.FinishTurnAsync = (_, _) =>
        {
            if (++finished == 1)
            {
                if (followUp) agent.FollowUp(new UserMessage("queued"));
                else agent.Steer(new UserMessage("queued"));
            }
            return Task.FromResult<AgentTurnDecision?>(null);
        };
        agent.PrepareNextTurnAsync = (_, _) => Task.FromResult<AgentLoopTurnUpdate?>(new(Tools: [new TestTool("second")]));
        var events = new List<AgentEvent>();
        agent.Subscribe(events.Add);
        await agent.PromptAsync("one");
        var added = Assert.IsType<AgentEndEvent>(events.Last()).Messages;
        Assert.Equal(new[] { "user", "assistant", "system", "user", "assistant" }, added.Select(message => message.Role));
        Assert.Equal("second", Assert.Single(provider.Contexts[1].Tools!).Name);
        Assert.Equal(2, events.OfType<TurnStartEvent>().Count());
    }

    /// <summary>上下文转换可以移除模型可见工具，运行时不会从可执行集合暗中补回。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task ConvertToLlm_ControlsVisibleDeclarations()
    {
        var provider = new Provider();
        var agent = CreateAgent(provider, [new TestTool("tool")]);
        agent.ConvertToLlm = messages => messages.Where(message => message is not SystemMessage).ToArray();
        await agent.PromptAsync("one");
        Assert.Empty(Assert.Single(provider.Contexts).Tools!);
        Assert.Single(Transcript.GetCurrentTools(agent.State.Messages));
        Assert.Single(agent.State.Tools);
    }

    /// <summary>失败、取消或上下文替换后，结束事件仍准确保留本次输入，排除旧历史。</summary>
    /// <param name="failure">模拟终态。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData("error")]
    [InlineData("aborted")]
    [InlineData("throw")]
    [InlineData("replacement")]
    public async Task AgentEnd_ContainsRunDeltaAcrossFailureAndReplacement(string failure)
    {
        var provider = new Provider { Failure = failure };
        var history = new UserMessage("history");
        var agent = CreateAgent(provider, [], [history]);
        if (failure == "replacement") agent.PrepareRequestAsync = (_, _) => Task.FromResult<AgentRequestUpdate?>(new(Context: [new UserMessage("canonical")]));
        var events = new List<AgentEvent>();
        agent.Subscribe(events.Add);
        var prompt = new UserMessage("current");
        await agent.PromptAsync(prompt);
        var end = Assert.Single(events.OfType<AgentEndEvent>());
        Assert.Equal(2, end.Messages.Count);
        Assert.Same(prompt, end.Messages[0]);
        Assert.IsType<AssistantMessage>(end.Messages[1]);
        Assert.DoesNotContain(history, end.Messages);
        Assert.DoesNotContain(end.Messages.OfType<UserMessage>(), message => message.Content.OfType<TextContent>().Any(text => text.Text == "canonical"));
        if (failure != "replacement") Assert.NotNull(end.ErrorMessage);
    }

    /// <summary>低层输入通过 InitialMessages 明确区分历史，RunStream 结果与结束事件一致。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task RunStream_SeparatesInitialMessagesFromHistory()
    {
        var provider = new Provider();
        var config = Config(provider);
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("history"));
        var prompt = new UserMessage("current");
        var stream = runtime.RunStream(config with { InitialMessages = [prompt] });
        var events = new List<AgentEvent>();
        await foreach (var evt in stream) events.Add(evt);
        var result = await stream.ResultAsync;
        Assert.Equal(2, result.Length);
        Assert.Same(prompt, result[0]);
        Assert.Equal(result, Assert.Single(events.OfType<AgentEndEvent>()).Messages);
        Assert.Equal(3, runtime.State.Messages.Count);
        Assert.Equal(result, events.OfType<MessageEndEvent>().Select(evt => evt.Message));
    }

    /// <summary>JSONL 与旧版平面会话保存系统增量，恢复后不再次声明相同工具。</summary>
    /// <returns>测试任务。</returns>
    [Fact]
    public async Task Persistence_RestoresToolDeclarationsWithoutDuplicates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-transcript-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new Provider();
            var agent = CreateAgent(provider, [new TestTool("tool")]);
            await agent.PromptAsync([new SystemMessage("instruction") { Sections = new Dictionary<string, string?> { ["a"] = "value", ["removed"] = null } }, new UserMessage("one")]);
            var flat = new CodingAgentSessionStore(Path.Combine(directory, "flat.json"));
            flat.Save(agent.State.Messages);
            var restored = flat.LoadStrict().Messages;
            Assert.True(Transcript.DeclarationsEqual(Assert.Single(Transcript.GetCurrentTools(agent.State.Messages)), Assert.Single(Transcript.GetCurrentTools(restored))));
            var storage = await JsonlSessionStorage.CreateAsync(Path.Combine(directory, "tree.jsonl"), directory, "session");
            string? parent = null;
            for (var i = 0; i < restored.Count; i++)
            {
                var id = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await storage.AppendEntryAsync(new MessageSessionEntry(id, parent, DateTimeOffset.UtcNow, restored[i]));
                parent = id;
            }
            var reopened = await JsonlSessionStorage.OpenAsync(Path.Combine(directory, "tree.jsonl"));
            var messages = (await reopened.GetEntriesAsync()).OfType<MessageSessionEntry>().Select(entry => entry.Message).ToArray();
            Assert.Equal("base prompt\n\ninstruction\n\nvalue", Transcript.GetCurrentSystemPrompt(messages));
            Assert.Contains("removed", Assert.IsType<SystemMessage>(messages[1]).Sections!.Keys);
            var next = CreateAgent(new Provider(), [new TestTool("tool")], messages);
            var ends = new List<AgentEndEvent>();
            next.Subscribe(evt => { if (evt is AgentEndEvent end) ends.Add(end); });
            await next.PromptAsync("two");
            Assert.DoesNotContain(Assert.Single(ends).Messages, message => message is SystemMessage);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>真实 Node 的消息结束钩子能修改系统文本，并完整保留工具、段落与时间。</summary>
    [Fact]
    public void Extension_SystemMessageRoundTripPreservesDeclarations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-system-extension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "system.cjs");
            File.WriteAllText(file, """
                module.exports = pi => {
                  pi.on("message_end", event => {
                    if (event.message.role !== "system") throw new Error("wrong role");
                    if (event.message.toolsAdded[0].name !== "tool") throw new Error("missing tool");
                    return { message: { ...event.message, content: "patched" } };
                  });
                };
                """);
            using var runtime = new CodingAgentJavaScriptExtensionRuntime(directory);
            Assert.True(runtime.Load(file).Success);
            var message = new SystemMessage("original")
            {
                ToolsAdded = [new Tool("tool", "description", Json("{\"type\":\"object\"}"))],
                ToolsRemoved = [new("old")], Sections = new Dictionary<string, string?> { ["note"] = "section", ["removed"] = null },
                Timestamp = DateTimeOffset.UnixEpoch
            };
            using var payload = JsonDocument.Parse("{\"type\":\"message_end\",\"message\":" + Tau.Ai.Serialization.SystemMessageJson.ToElement(message).GetRawText() + "}");
            var response = runtime.EmitEvent(file, payload.RootElement);
            Assert.True(response.Success, response.Error);
            Assert.Empty(response.HandlerErrors);
            var replacement = Assert.IsType<SystemMessage>(response.ReplacementMessage);
            Assert.Equal("patched", replacement.Content);
            Assert.Equal(message.Timestamp, replacement.Timestamp);
            Assert.Equal("section", replacement.Sections!["note"]);
            Assert.Null(replacement.Sections["removed"]);
            Assert.Equal("old", Assert.Single(replacement.ToolsRemoved!).Name);
            Assert.True(Transcript.DeclarationsEqual(Assert.Single(message.ToolsAdded), Assert.Single(replacement.ToolsAdded!)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>真实 Agent 跨轮切换工具后，Mistral 使用当前集合，网关保留增量，Reset 折叠成新基线。</summary>
    /// <param name="gateway">是否使用网关协议。</param>
    /// <returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeProviders_PreserveToolChangesAcrossTurnsAndReset(bool gateway)
    {
        using var handler = new TranscriptHttpHandler { PiMessages = gateway };
        using var client = new HttpClient(handler);
        IStreamProvider provider = gateway ? new PiMessagesProvider(client) : new MistralProvider(client);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var agent = new Agent(new()
        {
            Model = new() { Id = "test", Name = "Test", Api = provider.Api, Provider = "transcript-test", BaseUrl = "https://example.invalid/v1",
                Compat = new() { SupportsMidConvoSystemMessages = true } },
            ProviderRegistry = registry, SystemPrompt = "base", Tools = [new TestTool("first")],
            StreamOptions = new() { ApiKey = "test" }
        });
        var ends = new List<AgentEndEvent>();
        agent.Subscribe(value => { if (value is AgentEndEvent end) ends.Add(end); });
        await agent.PromptAsync("one");
        agent.Tools = [new TestTool("second")];
        await agent.PromptAsync([new SystemMessage("later") { Sections = new Dictionary<string, string?> { ["note"] = "section" } }, new UserMessage("two")]);
        Assert.Null(agent.State.ErrorMessage);
        Assert.Equal(2, handler.Bodies.Count);
        var second = handler.Bodies[1];
        var messages = (gateway ? second.GetProperty("context") : second).GetProperty("messages");
        var systems = messages.EnumerateArray().Where(message => message.GetProperty("role").GetString() == "system").ToArray();
        Assert.Equal(2, systems.Length);
        if (gateway)
        {
            Assert.Equal("first", systems[0].GetProperty("toolsAdded")[0].GetProperty("name").GetString());
            Assert.Equal("second", systems[1].GetProperty("toolsAdded")[0].GetProperty("name").GetString());
            Assert.Equal("first", systems[1].GetProperty("toolsRemoved")[0].GetProperty("name").GetString());
            Assert.Equal("section", systems[1].GetProperty("sections").GetProperty("note").GetString());
        }
        else
        {
            Assert.Equal("second", second.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
            Assert.Contains("Updated system prompt section", systems[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        }
        agent.Reset();
        await agent.PromptAsync("fresh");
        Assert.Null(agent.State.ErrorMessage);
        var reset = gateway ? handler.Bodies[2].GetProperty("context") : handler.Bodies[2];
        var baseline = Assert.Single(reset.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "system");
        Assert.Equal(gateway ? "base\n\nlater" : "base\n\nlater\n\nsection", baseline.GetProperty("content").GetString());
        if (gateway)
        {
            Assert.Equal("second", baseline.GetProperty("toolsAdded")[0].GetProperty("name").GetString());
            Assert.False(baseline.TryGetProperty("toolsRemoved", out _));
        }
        Assert.Equal([2, 3, 2], ends.Select(end => end.Messages.Count));
    }

    /// <summary>创建使用可记录 provider 的代理。</summary>
    /// <param name="provider">模拟 provider。</param>
    /// <param name="tools">初始工具。</param>
    /// <param name="history">既有历史。</param>
    /// <returns>代理实例。</returns>
    private static Agent CreateAgent(Provider provider, IReadOnlyList<IAgentTool> tools, IReadOnlyList<ChatMessage>? history = null)
    {
        var config = Config(provider);
        return new(new AgentOptions { Model = config.Model, ProviderRegistry = config.ProviderRegistry, SystemPrompt = "base prompt", Tools = tools, Messages = history ?? [] });
    }

    /// <summary>建立隔离的循环配置。</summary>
    /// <param name="provider">模拟 provider。</param>
    /// <returns>循环配置。</returns>
    private static AgentLoopConfig Config(Provider provider)
    {
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        return new() { Model = new() { Id = "model", Name = "Model", Provider = "test", Api = provider.Api }, ProviderRegistry = registry, Tools = [] };
    }

    /// <summary>解析独立 JSON 节点。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>节点副本。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private sealed class TranscriptHttpHandler : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];
        public bool Anthropic { get; init; }
        public bool PiMessages { get; init; }
        /// <summary>记录真实请求并返回完成的聊天事件，不访问网络。</summary>
        /// <param name="request">实际 HTTP 请求。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>模拟 SSE 响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Bodies.Add(body.RootElement.Clone());
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(PiMessages ? "data: {\"type\":\"done\",\"reason\":\"stop\",\"usage\":{\"input\":1,\"output\":1}}\n\n" : Anthropic ? """
                    event: message_start
                    data: {"type":"message_start","message":{"id":"test","usage":{"input_tokens":1,"output_tokens":0}}}

                    event: content_block_start
                    data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":"ok"}}

                    event: content_block_stop
                    data: {"type":"content_block_stop","index":0}

                    event: message_delta
                    data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

                    event: message_stop
                    data: {"type":"message_stop"}

                    """ : "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n", System.Text.Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class Provider : IStreamProvider
    {
        public string Api => "transcript-test";
        public string? Failure { get; init; }
        public List<LlmContext> Contexts { get; } = [];
        /// <summary>记录上下文并产生选定的终态。</summary>
        /// <param name="model">模型。</param>
        /// <param name="context">已转换上下文。</param>
        /// <param name="options">选项。</param>
        /// <returns>模拟流。</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            Contexts.Add(context with { Messages = context.Messages.ToArray() });
            if (Failure == "throw") throw new InvalidOperationException("failed");
            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent("done")])
            {
                StopReason = Failure == "error" ? StopReason.Error : Failure == "aborted" ? StopReason.Aborted : StopReason.EndTurn,
                ErrorMessage = Failure is "error" or "aborted" ? "failed" : null
            }));
            return stream;
        }
        /// <summary>复用标准模拟请求。</summary>
        /// <param name="model">模型。</param>
        /// <param name="context">上下文。</param>
        /// <param name="options">选项。</param>
        /// <returns>模拟流。</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);
    }

    private sealed class TestTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description { get; set; } = "tool description";
        public JsonElement ParameterSchema { get; set; } = Json("{\"type\":\"object\"}");
        public ConstrainedSamplingConfig? ConstrainedSampling { get; set; }
        /// <summary>此测试只验证声明，不允许实际执行工具。</summary>
        /// <param name="toolCallId">调用标识。</param>
        /// <param name="args">参数。</param>
        /// <param name="ct">取消信号。</param>
        /// <param name="onUpdate">更新回调。</param>
        /// <returns>不会正常返回。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null) => throw new InvalidOperationException("Unexpected tool call");
    }
}
