// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Tests;

/// <summary>【AgentCore】【回合契约】对照 pi 的请求准备和回合完成行为</summary>
public sealed class AgentTurnHookTests
{
    /// <summary>【AgentCore】【队列预览】预览只选择优先队列并遵守模式，修改返回数组不会污染队列</summary>
    /// <param name="steeringMode">引导消息消费模式</param>
    /// <param name="followUpMode">后续消息消费模式</param>
    [Theory]
    [InlineData(AgentQueueMode.OneAtATime, AgentQueueMode.OneAtATime)]
    [InlineData(AgentQueueMode.OneAtATime, AgentQueueMode.All)]
    [InlineData(AgentQueueMode.All, AgentQueueMode.OneAtATime)]
    [InlineData(AgentQueueMode.All, AgentQueueMode.All)]
    public void PeekQueuedMessages_PreservesSelectionAndQueueOwnership(AgentQueueMode steeringMode, AgentQueueMode followUpMode)
    {
        var runtime = new AgentRuntime { SteeringMode = steeringMode, FollowUpMode = followUpMode };
        ChatMessage[] steering = [new UserMessage("s1"), new UserMessage("s2")];
        ChatMessage[] followUp = [new UserMessage("f1"), new UserMessage("f2")];
        runtime.FollowUp(followUp[0]);
        runtime.Steer(steering[0]);
        runtime.FollowUp(followUp[1]);
        runtime.Steer(steering[1]);

        // 1. 【AgentCore】【队列预览】交错入队仍优先选择引导，预览不改变待处理数量
        var expectedSteering = steering.Take(steeringMode == AgentQueueMode.All ? 2 : 1).ToArray();
        var preview = runtime.PeekQueuedMessages();
        Assert.Equal(expectedSteering, preview);
        Assert.Equal(4, runtime.PendingMessageCount);
        Assert.True(runtime.HasQueuedMessages);
        Assert.IsType<ChatMessage[]>(preview)[0] = new UserMessage("changed snapshot");
        Assert.Equal(expectedSteering, runtime.PeekQueuedMessages());
        Assert.Equal(expectedSteering, runtime.DrainSteeringMessages());
        Assert.Equal(4 - expectedSteering.Length, runtime.PendingMessageCount);

        // 2. 【AgentCore】【队列预览】清空引导后预览后续队列，旧快照不随清空改变
        runtime.ClearSteeringQueue();
        var expectedFollowUp = followUp.Take(followUpMode == AgentQueueMode.All ? 2 : 1).ToArray();
        var followUpPreview = runtime.PeekQueuedMessages();
        Assert.Equal(expectedFollowUp, followUpPreview);
        Assert.Equal(expectedFollowUp, runtime.DrainFollowUpMessages());
        runtime.ClearFollowUpQueue();
        Assert.Equal(expectedFollowUp, followUpPreview);
        Assert.Empty(runtime.PeekQueuedMessages());
        Assert.Equal(0, runtime.PendingMessageCount);
        Assert.False(runtime.HasQueuedMessages);
    }

    /// <summary>【AgentCore】【队列预览】公开代理中的预览与下一次模型请求一致，反复预览不重复投递</summary>
    /// <param name="steeringMode">引导消息消费模式</param>
    /// <param name="followUpMode">后续消息消费模式</param>
    /// <returns>模拟模型运行和队列契约验证任务</returns>
    [Theory]
    [InlineData(AgentQueueMode.OneAtATime, AgentQueueMode.OneAtATime)]
    [InlineData(AgentQueueMode.OneAtATime, AgentQueueMode.All)]
    [InlineData(AgentQueueMode.All, AgentQueueMode.OneAtATime)]
    [InlineData(AgentQueueMode.All, AgentQueueMode.All)]
    public async Task Agent_PeekQueuedMessages_MatchesNextRequests(AgentQueueMode steeringMode, AgentQueueMode followUpMode)
    {
        var provider = new RecordingProvider();
        var config = CreateConfig(provider);
        var agent = new Agent(new AgentOptions
        {
            Model = config.Model, ProviderRegistry = config.ProviderRegistry,
            SteeringMode = steeringMode, FollowUpMode = followUpMode
        });
        ChatMessage[] queued = [new UserMessage("s1"), new UserMessage("s2"), new UserMessage("f1"), new UserMessage("f2")];
        var previews = new List<IReadOnlyList<ChatMessage>>();
        agent.FinishTurnAsync = (_, _) =>
        {
            if (provider.Calls.Count == 1)
            {
                agent.FollowUp(queued[2]);
                agent.Steer(queued[0]);
                agent.FollowUp(queued[3]);
                agent.Steer(queued[1]);
            }
            var selected = agent.PeekQueuedMessages();
            Assert.Equal(selected, agent.PeekQueuedMessages());
            if (selected.Count > 0) previews.Add(selected);
            return Task.FromResult<AgentTurnDecision?>(null);
        };
        await agent.PromptAsync("start");
        Assert.Null(agent.State.ErrorMessage);
        Assert.Equal(1 + (steeringMode == AgentQueueMode.All ? 1 : 2) + (followUpMode == AgentQueueMode.All ? 1 : 2), provider.Calls.Count);
        Assert.Equal(provider.Calls.Count - 1, previews.Count);
        for (var index = 1; index < provider.Calls.Count; index++)
        {
            var previousMessages = provider.Calls[index - 1].Context.Messages;
            var newlyQueued = provider.Calls[index].Context.Messages.Where(message => queued.Contains(message) && !previousMessages.Contains(message));
            Assert.Equal(previews[index - 1], newlyQueued);
        }
        Assert.Equal(queued, agent.State.Messages.Where(queued.Contains));
        Assert.Empty(agent.PeekQueuedMessages());
        Assert.False(agent.HasQueuedMessages);
    }

    /// <summary>首次请求在队列消息发布后替换状态，新增消息记录不混入替换上下文</summary>
    /// <returns>契约验证任务</returns>
    [Fact]
    public async Task PrepareRequest_ReplacesInitialStateAfterQueuedMessages()
    {
        var provider = new RecordingProvider();
        var runtime = new AgentRuntime();
        runtime.AddMessage(new UserMessage("history"));
        var steering = new UserMessage("steering");
        var canonical = new UserMessage("canonical");
        runtime.Steer(steering);
        var events = new List<AgentEvent>();
        var config = CreateConfig(provider);
        var replacement = config.Model with { Id = "replacement", Provider = "next-provider" };
        var tool = new TestTool();
        AgentLoopTurnContext? completed = null;
        var prepared = 0;
        config = config with
        {
            PrepareRequestAsync = (request, token) =>
            {
                prepared++;
                Assert.True(token.CanBeCanceled);
                Assert.Contains(events.OfType<MessageEndEvent>(), evt => ReferenceEquals(evt.Message, steering));
                Assert.Contains(steering, request.Context);
                Assert.Equal(ThinkingLevel.Off, request.ThinkingLevel);
                return Task.FromResult<AgentRequestUpdate?>(new(
                    Context: [canonical], Model: replacement, SystemPrompt: "updated", Tools: [tool], Reasoning: ThinkingLevel.High));
            },
            TransformContext = messages =>
            {
                Assert.Same(canonical, messages[0]);
                Assert.IsType<SystemMessage>(messages[1]);
                return messages;
            },
            GetApiKeyAsync = (name, _) =>
            {
                Assert.Equal("next-provider", name);
                return Task.FromResult<string?>("new-key");
            },
            FinishTurnAsync = (turn, _) => { completed = turn; return Task.FromResult<AgentTurnDecision?>(null); }
        };
        await CollectAsync(runtime.RunAsync(config), events);

        Assert.Equal(1, prepared);
        var call = Assert.Single(provider.Calls);
        Assert.Same(replacement, call.Model);
        Assert.Equal([canonical], call.Context.Messages);
        Assert.Equal("updated", call.Context.SystemPrompt);
        Assert.Equal([tool.Name], call.Context.Tools!.Select(item => item.Name));
        Assert.Equal(ThinkingLevel.High, call.Options.Reasoning);
        Assert.Equal("new-key", call.Options.ApiKey);
        Assert.NotNull(completed);
        Assert.Equal(3, completed.NewMessages.Count);
        Assert.Same(steering, completed.NewMessages[0]);
        Assert.DoesNotContain(canonical, completed.NewMessages);
        Assert.Equal("updated", completed.SystemPrompt);
        Assert.Same(tool, Assert.Single(completed.Tools));
    }

    /// <summary>请求准备期间入队的 steering 只会进入下一次正常请求</summary>
    /// <returns>契约验证任务</returns>
    [Fact]
    public async Task PrepareRequest_DoesNotPollSteeringAgain()
    {
        var provider = new RecordingProvider();
        var runtime = new AgentRuntime();
        var late = new UserMessage("late");
        var preparations = 0;
        var config = CreateConfig(provider) with
        {
            PrepareRequestAsync = (_, _) =>
            {
                if (++preparations == 1) runtime.Steer(late);
                return Task.FromResult<AgentRequestUpdate?>(null);
            }
        };
        await CollectAsync(runtime.RunAsync(config));
        Assert.Equal(2, provider.Calls.Count);
        Assert.DoesNotContain(late, provider.Calls[0].Context.Messages);
        Assert.Contains(late, provider.Calls[1].Context.Messages);
        Assert.Equal(2, preparations);
    }

    /// <summary>回合完成钩子位于最终工具消息之后、turn_end 之前，End 保留队列并跳过后续准备</summary>
    /// <returns>契约验证任务</returns>
    [Fact]
    public async Task FinishTurn_ObservesFinalToolResultsBeforeTurnEnd()
    {
        var provider = new RecordingProvider { Response = _ => new DoneEvent(ToolMessage()) };
        var runtime = new AgentRuntime();
        var order = new List<string>();
        var tool = new TestTool();
        var config = CreateConfig(provider, tool) with
        {
            MessageEndTransformAsync = (message, _) => Task.FromResult(message is ToolResultMessage result
                ? (ChatMessage)(result with { Content = [new TextContent("final")] }) : message),
            FinishTurnAsync = (turn, _) =>
            {
                order.Add("finish");
                var result = Assert.Single(turn.ToolResults);
                Assert.Equal("final", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
                Assert.Same(result, turn.Context.Last());
                Assert.Same(result, runtime.State.Messages.Last());
                runtime.Steer(new UserMessage("queued steer"));
                runtime.FollowUp(new UserMessage("queued follow"));
                return Task.FromResult<AgentTurnDecision?>(AgentTurnDecision.End);
            },
            PrepareNextTurnAsync = (_, _) => throw new InvalidOperationException("must not prepare after end")
        };
        await foreach (var evt in runtime.RunAsync(config))
        {
            if (evt is MessageEndEvent { Message: ToolResultMessage }) order.Add("tool_result");
            if (evt is TurnEndEvent) order.Add("turn_end");
        }
        Assert.Equal(["tool_result", "finish", "turn_end"], order);
        Assert.Single(provider.Calls);
        Assert.Equal(2, runtime.PendingMessageCount);
    }

    /// <summary>显式继续只保证一次请求，工具或队列带来的自然请求不会再额外追加一次</summary>
    /// <param name="source">下一次请求的来源</param>
    /// <returns>契约验证任务</returns>
    [Theory]
    [InlineData("context")]
    [InlineData("tool")]
    [InlineData("steering")]
    [InlineData("follow-up")]
    [InlineData("terminating-tool")]
    public async Task FinishTurn_ContinueProducesExactlyOneNextRequest(string source)
    {
        var provider = new RecordingProvider
        {
            Response = count => new DoneEvent(count == 1 && source.Contains("tool") ? ToolMessage() : TextMessage())
        };
        var runtime = new AgentRuntime();
        var finishes = 0;
        var preparations = 0;
        var config = CreateConfig(provider, new TestTool { Terminate = source == "terminating-tool" }) with
        {
            FinishTurnAsync = (_, _) =>
            {
                if (++finishes != 1) return Task.FromResult<AgentTurnDecision?>(null);
                if (source == "steering") runtime.Steer(new UserMessage("queued"));
                if (source == "follow-up") runtime.FollowUp(new UserMessage("queued"));
                return Task.FromResult<AgentTurnDecision?>(AgentTurnDecision.Continue);
            },
            PrepareNextTurnAsync = (_, _) =>
            {
                preparations++;
                return Task.FromResult<AgentLoopTurnUpdate?>(null);
            }
        };
        await CollectAsync(runtime.RunAsync(config));
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(2, finishes);
        Assert.Equal(1, preparations);
    }

    /// <summary>错误和中止响应仍调用完成钩子，但忽略继续决策且不执行响应中的工具</summary>
    /// <param name="reason">失败终止原因</param>
    /// <param name="errorEvent">是否通过 ErrorEvent 而非 DoneEvent 交付失败</param>
    /// <returns>契约验证任务</returns>
    [Theory]
    [InlineData(StopReason.Error, false)]
    [InlineData(StopReason.Aborted, false)]
    [InlineData(StopReason.Error, true)]
    [InlineData(StopReason.Aborted, true)]
    public async Task FinishTurn_HardExitsIgnoreContinue(StopReason reason, bool errorEvent)
    {
        var failure = ToolMessage() with { StopReason = reason, ErrorMessage = "failed" };
        var provider = new RecordingProvider
        {
            Response = _ => errorEvent ? new ErrorEvent("failed", Message: failure) : new DoneEvent(failure)
        };
        var runtime = new AgentRuntime();
        var tool = new TestTool();
        var order = new List<string>();
        var finishes = 0;
        var config = CreateConfig(provider, tool) with
        {
            FinishTurnAsync = (turn, _) =>
            {
                finishes++;
                order.Add("finish");
                Assert.Equal(reason, turn.Message.StopReason);
                Assert.Same(turn.Message, turn.Context.Last());
                Assert.Empty(turn.ToolResults);
                runtime.Steer(new UserMessage("keep steer"));
                runtime.FollowUp(new UserMessage("keep follow"));
                return Task.FromResult<AgentTurnDecision?>(AgentTurnDecision.Continue);
            },
            PrepareNextTurnAsync = (_, _) => throw new InvalidOperationException("must not prepare after failure")
        };
        await foreach (var evt in runtime.RunAsync(config))
        {
            if (evt is MessageEndEvent { Message: AssistantMessage }) order.Add("message_end");
            if (evt is TurnEndEvent) order.Add("turn_end");
        }
        Assert.Equal(["message_end", "finish", "turn_end"], order);
        Assert.Equal(1, finishes);
        Assert.Equal(0, tool.Calls);
        Assert.Single(provider.Calls);
        Assert.Equal(2, runtime.PendingMessageCount);
    }

    /// <summary>上下文转换或模型流取消时，完成钩子观察到中止响应与原运行取消信号</summary>
    /// <param name="duringContext">是否在上下文转换阶段取消</param>
    /// <returns>契约验证任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishTurn_ReceivesCancelledRunToken(bool duringContext)
    {
        var provider = new RecordingProvider { Response = _ => null };
        var runtime = new AgentRuntime();
        var contextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        CancellationToken requestToken = default;
        var finishes = 0;
        var config = CreateConfig(provider) with
        {
            PrepareRequestAsync = (_, token) => { requestToken = token; return Task.FromResult<AgentRequestUpdate?>(null); },
            TransformContextAsync = async (messages, token) =>
            {
                if (duringContext)
                {
                    contextStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return messages;
            },
            FinishTurnAsync = (turn, token) =>
            {
                finishes++;
                Assert.Equal(requestToken, token);
                Assert.True(token.IsCancellationRequested);
                Assert.Equal(StopReason.Aborted, turn.Message.StopReason);
                return Task.FromResult<AgentTurnDecision?>(AgentTurnDecision.Continue);
            }
        };
        var running = CollectAsync(runtime.RunAsync(config, cancellation.Token));
        await (duringContext ? contextStarted.Task : provider.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var events = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, finishes);
        Assert.Single(events.OfType<TurnEndEvent>());
        Assert.IsType<AgentEndEvent>(events.Last());
        Assert.False(runtime.State.IsStreaming);
    }

    /// <summary>下一回合准备追加的消息先于已选择的 steering 发布，准备期间的新消息留待后续回合</summary>
    /// <returns>契约验证任务</returns>
    [Fact]
    public async Task PrepareNextTurn_PreservesSelectedSteeringAndEmitsPreparedMessages()
    {
        var provider = new RecordingProvider();
        var runtime = new AgentRuntime();
        var first = new UserMessage("first");
        var second = new UserMessage("second");
        var during = new UserMessage("during preparation");
        var prepared = new UserMessage("prepared");
        var events = new List<AgentEvent>();
        var finishes = 0;
        var preparations = 0;
        var config = CreateConfig(provider) with
        {
            FinishTurnAsync = (_, _) =>
            {
                if (++finishes == 1)
                {
                    runtime.Steer(first);
                    runtime.Steer(second);
                    return Task.FromResult<AgentTurnDecision?>(null);
                }
                return Task.FromResult<AgentTurnDecision?>(AgentTurnDecision.End);
            },
            PrepareNextTurnAsync = (_, _) =>
            {
                Assert.Equal(1, ++preparations);
                runtime.Steer(during);
                return Task.FromResult<AgentLoopTurnUpdate?>(new() { Messages = [prepared] });
            },
            PrepareRequestAsync = (request, _) =>
            {
                if (provider.Calls.Count == 1)
                {
                    Assert.Equal([prepared, first], request.Context.TakeLast(2));
                    Assert.Equal([prepared, first], events.OfType<MessageEndEvent>().TakeLast(2).Select(evt => evt.Message));
                }
                return Task.FromResult<AgentRequestUpdate?>(null);
            }
        };
        await CollectAsync(runtime.RunAsync(config), events);
        Assert.Equal(2, provider.Calls.Count);
        Assert.DoesNotContain(second, provider.Calls[1].Context.Messages);
        Assert.DoesNotContain(during, provider.Calls[1].Context.Messages);
        Assert.Equal([second, during], runtime.DrainSteeringMessages().Concat(runtime.DrainSteeringMessages()));
    }

    /// <summary>工具后的准备阶段可接收新 steering，推理 Off 清除上一请求的推理等级</summary>
    /// <returns>契约验证任务</returns>
    [Fact]
    public async Task PrepareNextTurn_PicksUpNewSteeringWhenNoneWasSelected()
    {
        var provider = new RecordingProvider { Response = n => new DoneEvent(n == 1 ? ToolMessage() : TextMessage()) };
        var runtime = new AgentRuntime();
        var during = new UserMessage("during preparation");
        var requests = 0;
        var config = CreateConfig(provider, new TestTool()) with
        {
            StreamOptions = new SimpleStreamOptions { Reasoning = ThinkingLevel.High },
            PrepareNextTurnAsync = (_, _) =>
            {
                runtime.Steer(during);
                return Task.FromResult<AgentLoopTurnUpdate?>(null);
            },
            PrepareRequestAsync = (request, _) =>
            {
                Assert.Equal(ThinkingLevel.High, request.ThinkingLevel);
                return Task.FromResult<AgentRequestUpdate?>(++requests == 2 ? new(Reasoning: ThinkingLevel.Off) : null);
            }
        };
        await CollectAsync(runtime.RunAsync(config));
        Assert.Equal(2, provider.Calls.Count);
        Assert.Contains(during, provider.Calls[1].Context.Messages);
        Assert.Null(provider.Calls[1].Options.Reasoning);
        Assert.Equal(["high", "off"], runtime.State.Messages.OfType<AssistantMessage>().Select(message => message.ThinkingLevel));
    }

    /// <summary>公开 Agent 选项与属性均转发钩子，新消息快照包含提示但排除旧历史</summary>
    /// <param name="useProperties">是否在创建后通过公开属性设置钩子</param>
    /// <returns>契约验证任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agent_ForwardsHooksAndSeparatesNewMessagesFromHistory(bool useProperties)
    {
        var provider = new RecordingProvider();
        var config = CreateConfig(provider);
        var history = new UserMessage("history");
        var prompt = new UserMessage("prompt");
        var follow = new UserMessage("follow");
        var turns = new List<AgentLoopTurnContext>();
        var preparations = 0;
        Func<AgentPrepareRequestContext, CancellationToken, Task<AgentRequestUpdate?>> prepare = (_, _) =>
        {
            preparations++;
            return Task.FromResult<AgentRequestUpdate?>(null);
        };
        Func<AgentLoopTurnContext, CancellationToken, Task<AgentTurnDecision?>> finish = (turn, _) =>
        {
            turns.Add(turn);
            return Task.FromResult<AgentTurnDecision?>(AgentTurnDecision.End);
        };
        var agent = new Agent(new AgentOptions
        {
            Model = config.Model, ProviderRegistry = config.ProviderRegistry, Messages = [history],
            PrepareRequestAsync = useProperties ? null : prepare,
            FinishTurnAsync = useProperties ? null : finish,
            ShouldStopAfterTurnAsync = (_, _) => throw new InvalidOperationException("legacy callback must not run")
        });
        if (useProperties)
        {
            agent.PrepareRequestAsync = prepare;
            agent.FinishTurnAsync = finish;
        }
        await agent.PromptAsync(prompt);
        agent.FollowUp(follow);
        await agent.ContinueAsync();
        Assert.Equal(2, preparations);
        Assert.Equal(2, turns.Count);
        Assert.Same(prompt, turns[0].NewMessages[0]);
        Assert.Equal(2, turns[0].NewMessages.Count);
        Assert.Same(follow, turns[1].NewMessages[0]);
        Assert.Equal(2, turns[1].NewMessages.Count);
        Assert.DoesNotContain(history, turns[0].NewMessages);
        Assert.DoesNotContain(prompt, turns[1].NewMessages);
        Assert.Equal(5, turns[1].Context.Count);
        Assert.Null(agent.State.ErrorMessage);
    }

    /// <summary>准备或完成钩子失败时通过公开 Agent 输出错误，且不会重复触发完成钩子</summary>
    /// <param name="failDuringPreparation">是否由请求准备钩子抛出异常</param>
    /// <returns>契约验证任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agent_HookFailuresEndRunWithoutReinvocation(bool failDuringPreparation)
    {
        var provider = new RecordingProvider();
        var config = CreateConfig(provider);
        var finishes = 0;
        var agent = new Agent(new AgentOptions
        {
            Model = config.Model, ProviderRegistry = config.ProviderRegistry,
            PrepareRequestAsync = (_, _) => failDuringPreparation
                ? throw new InvalidOperationException("hook failed") : Task.FromResult<AgentRequestUpdate?>(null),
            FinishTurnAsync = (_, _) => { finishes++; throw new InvalidOperationException("hook failed"); }
        });
        var events = new List<AgentEvent>();
        using var subscription = agent.Subscribe(events.Add);
        await agent.PromptAsync("run");
        await agent.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(failDuringPreparation ? 0 : 1, finishes);
        Assert.Equal(failDuringPreparation ? 0 : 1, provider.Calls.Count);
        Assert.Equal("hook failed", agent.State.ErrorMessage);
        Assert.Single(events.OfType<AgentEndEvent>());
    }

    /// <summary>生成本组测试使用的运行配置</summary>
    /// <param name="provider">录制请求的模型提供方</param>
    /// <param name="tools">可执行工具</param>
    /// <returns>独立运行配置</returns>
    private static AgentLoopConfig CreateConfig(RecordingProvider provider, params IAgentTool[] tools)
    {
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);
        return new AgentLoopConfig
        {
            Model = new Model { Provider = "test", Id = "test", Name = "Test", Api = provider.Api },
            ProviderRegistry = registry,
            Tools = tools
        };
    }

    /// <summary>消费事件流并记录顺序</summary>
    /// <param name="source">待消费事件流</param>
    /// <param name="events">可选的外部事件记录</param>
    /// <returns>完整事件列表</returns>
    private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> source, List<AgentEvent>? events = null)
    {
        events ??= [];
        await foreach (var evt in source) events.Add(evt);
        return events;
    }

    /// <summary>创建普通助手响应</summary>
    /// <returns>正常结束的响应</returns>
    private static AssistantMessage TextMessage() => new([new TextContent("done")]) { StopReason = StopReason.EndTurn };

    /// <summary>创建调用测试工具的助手响应</summary>
    /// <returns>包含单个工具调用的响应</returns>
    private static AssistantMessage ToolMessage() => new([new ToolCallContent("call", "echo", "{}")]) { StopReason = StopReason.ToolUse };

    /// <summary>记录调用并输出指定终态事件的提供方</summary>
    private sealed class RecordingProvider : IStreamProvider
    {
        public string Api => "test-turn-hooks";
        public List<(Model Model, LlmContext Context, SimpleStreamOptions Options)> Calls { get; } = [];
        public Func<int, StreamEvent?> Response { get; init; } = _ => new DoneEvent(TextMessage());
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>通过标准选项创建模拟响应</summary>
        /// <param name="model">请求模型</param>
        /// <param name="context">请求上下文</param>
        /// <param name="options">请求选项</param>
        /// <returns>模拟事件流</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            StreamSimple(model, context, options as SimpleStreamOptions ?? new SimpleStreamOptions());

        /// <summary>记录请求快照，响应为空时保持流打开以测试取消</summary>
        /// <param name="model">请求模型</param>
        /// <param name="context">请求上下文</param>
        /// <param name="options">请求选项</param>
        /// <returns>模拟事件流</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
        {
            if (Calls.Count >= 5) throw new InvalidOperationException("unexpected extra request");
            Calls.Add((model, new LlmContext(context.SystemPrompt, context.Messages.ToArray(), context.Tools), options));
            var stream = new AssistantMessageStream();
            if (Response(Calls.Count) is { } response) stream.Push(response);
            Started.TrySetResult();
            return stream;
        }
    }

    /// <summary>可配置终止标志的测试工具</summary>
    private sealed class TestTool : IAgentTool
    {
        public string Name => "echo";
        public string Label => "Echo";
        public string Description => "Echo";
        public JsonElement ParameterSchema { get; } = JsonDocument.Parse("{}").RootElement.Clone();
        public bool Terminate { get; init; }
        public int Calls { get; private set; }

        /// <summary>记录调用并返回可选的终止结果</summary>
        /// <param name="toolCallId">工具调用标识</param>
        /// <param name="args">工具参数</param>
        /// <param name="ct">取消信号</param>
        /// <param name="onUpdate">增量更新回调</param>
        /// <returns>工具执行结果</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
        {
            Calls++;
            return Task.FromResult(new ToolResult([new TextContent("raw")], Terminate: Terminate));
        }
    }
}
