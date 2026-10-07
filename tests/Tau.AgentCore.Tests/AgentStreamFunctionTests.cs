// 作者：xxx
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Tests;

/// <summary>【AgentCore】【流式默认值】修改进程后备函数的测试独占执行，避免影响其他请求夹具</summary>
[CollectionDefinition("Agent stream defaults", DisableParallelization = true)]
public sealed class AgentStreamDefaultsCollection;

/// <summary>【AgentCore】【流式接口】验证实例入口、异步初始化、失败、取消和默认函数捕获</summary>
[Collection("Agent stream defaults")]
public sealed class AgentStreamFunctionTests
{
    /// <summary>【AgentCore】【流式请求】发送入口在准备、转换和凭据解析之后执行，并允许异步返回响应流</summary>
    /// <param name="asynchronous">是否异步初始化流</param><returns>异步回归任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamFunction_ReceivesPreparedContextAndRequestOptions(bool asynchronous)
    {
        var order = new List<string>();
        var selected = Model("prepared");
        var runtime = new AgentRuntime();
        var config = new AgentLoopConfig
        {
            Model = Model("initial"), Tools = [], InitialMessages = [new UserMessage("original")],
            PrepareRequestAsync = (_, _) =>
            {
                order.Add("prepare");
                return Task.FromResult<AgentRequestUpdate?>(new(Model: selected, StreamOptions: new() { MaxTokens = 31 }, Reasoning: ThinkingLevel.High));
            },
            TransformContext = messages => { order.Add("transform"); return [.. messages, new UserMessage("transformed")]; },
            ConvertToLlm = messages => { order.Add("convert"); return [.. messages, new UserMessage("converted")]; },
            GetApiKeyAsync = (provider, _) => { Assert.Equal("prepared", provider); order.Add("key"); return Task.FromResult<string?>("fixture-key"); },
            StreamFunction = async (model, context, options) =>
            {
                if (asynchronous) await Task.Yield();
                order.Add("stream");
                Assert.Same(selected, model);
                Assert.Equal("converted", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(context.Messages.Last()).Content.Single()).Text);
                Assert.Equal("fixture-key", options.ApiKey);
                Assert.Equal(31, options.MaxTokens);
                Assert.Equal(ThinkingLevel.High, options.Reasoning);
                Assert.True(options.Signal.CanBeCanceled);
                return Response("custom response");
            },
            FinishTurnAsync = (_, _) => { order.Add("finish"); return Task.FromResult<AgentTurnDecision?>(null); }
        };
        var events = new List<AgentEvent>();
        await foreach (var item in runtime.RunAsync(config)) events.Add(item);
        Assert.Equal(["prepare", "transform", "convert", "key", "stream", "finish"], order);
        Assert.Null(Assert.Single(events.OfType<AgentEndEvent>()).ErrorMessage);
        Assert.Equal("high", Assert.Single(runtime.State.Messages.OfType<AssistantMessage>()).ThinkingLevel);
        Assert.Single(runtime.State.Messages.OfType<UserMessage>());
    }

    /// <summary>【AgentCore】【实例流式函数】实例函数优先于默认函数，公开属性替换只影响后续运行</summary>
    /// <returns>异步回归任务</returns>
    [Fact]
    public async Task Agent_StreamFunctionCanBeReplacedBetweenRuns()
    {
        AgentStreaming.SetDefaultStreamFunction((_, _, _) => throw new InvalidOperationException("default must not be used"));
        try
        {
            var agent = new Agent(new() { Model = Model("fixture"), StreamFunction = (_, _, _) => ValueTask.FromResult(Response("first")) });
            await agent.PromptAsync("first prompt");
            agent.StreamFunction = async (_, _, _) => { await Task.Yield(); return Response("second"); };
            await agent.PromptAsync("second prompt");
            Assert.Null(agent.State.ErrorMessage);
            Assert.Equal(["first", "second"], agent.State.Messages.OfType<AssistantMessage>().Select(message => Assert.IsType<TextContent>(Assert.Single(message.Content)).Text));
        }
        finally { AgentStreaming.SetDefaultStreamFunction(null); }
    }

    /// <summary>【AgentCore】【异步流失败】违反流内报错契约的发送函数也能结束公开代理，取消信号传入初始化等待</summary>
    /// <param name="failure">同步抛错、异步拒绝或取消</param><returns>异步回归任务</returns>
    [Theory]
    [InlineData("throw")]
    [InlineData("reject")]
    [InlineData("abort")]
    public async Task Agent_StreamFunctionFailureAndCancellationReleaseRun(string failure)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>【AgentCore】【失败夹具】按测试路径等待取消或拒绝异步初始化</summary>
        /// <param name="model">请求模型</param><param name="context">模型上下文</param><param name="options">含取消信号的选项</param>
        /// <returns>不会成功返回的响应流任务</returns>
        async ValueTask<AssistantMessageStream> FailAsync(Tau.Ai.Model model, LlmContext context, SimpleStreamOptions options)
        {
            entered.TrySetResult();
            if (failure == "abort") await Task.Delay(Timeout.Infinite, options.Signal);
            await Task.Yield();
            throw new InvalidOperationException("fixture stream failure");
        }
        var agent = new Agent(new()
        {
            Model = Model("fixture"), StreamFunction = failure == "throw"
                ? (_, _, _) => { entered.TrySetResult(); throw new InvalidOperationException("fixture stream failure"); } : FailAsync
        });
        var events = new List<AgentEvent>();
        using var subscription = agent.Subscribe(events.Add);
        var run = agent.PromptAsync("initial");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (failure == "abort") agent.Abort();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await agent.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(agent.State.IsStreaming);
        var result = Assert.Single(agent.State.Messages.OfType<AssistantMessage>());
        Assert.Equal(failure == "abort" ? StopReason.Aborted : StopReason.Error, result.StopReason);
        Assert.Single(events.OfType<AgentEndEvent>());
    }

    /// <summary>【AgentCore】【默认函数捕获】默认函数变更不改变已运行的多个回合，新运行使用新默认值</summary>
    /// <param name="lowLevel">是否使用低层运行时</param><returns>异步回归任务</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultStreamFunction_IsCapturedForWholeRun(bool lowLevel)
    {
        var calls = 0;
        Action? enqueue = null;
        AgentStreaming.SetDefaultStreamFunction((_, _, _) =>
        {
            if (++calls == 1)
            {
                AgentStreaming.SetDefaultStreamFunction((_, _, _) => ValueTask.FromResult(Response("new default")));
                enqueue!();
            }
            return ValueTask.FromResult(Response("captured"));
        });
        try
        {
            if (lowLevel)
            {
                var runtime = new AgentRuntime();
                enqueue = () => runtime.FollowUp(new UserMessage("next"));
                await foreach (var _ in runtime.RunAsync(new() { Model = Model("fixture"), Tools = [], InitialMessages = [new UserMessage("initial")] })) { }
            }
            else
            {
                var agent = new Agent(new() { Model = Model("fixture") });
                enqueue = () => agent.FollowUp(new UserMessage("next"));
                await agent.PromptAsync("initial");
                Assert.Null(agent.State.ErrorMessage);
            }
            Assert.Equal(2, calls);
            var next = new Agent(new() { Model = Model("fixture") });
            await next.PromptAsync("new run");
            Assert.Null(next.State.ErrorMessage);
            Assert.Equal("new default", Assert.IsType<TextContent>(Assert.Single(Assert.Single(next.State.Messages.OfType<AssistantMessage>()).Content)).Text);
        }
        finally { AgentStreaming.SetDefaultStreamFunction(null); }
    }

    /// <summary>【AgentCore】【流式夹具】生成没有注册表实现的模型，确保请求确实经自定义入口发送</summary>
    /// <param name="provider">测试提供方标识</param><returns>模型定义</returns>
    private static Tau.Ai.Model Model(string provider) => new() { Id = "fixture", Provider = provider, Api = "custom-stream", Name = "Fixture" };

    /// <summary>【AgentCore】【流式夹具】创建已完成的最小协议响应流</summary>
    /// <param name="text">助手文本</param><returns>包含终态的流</returns>
    private static AssistantMessageStream Response(string text)
    {
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent(text)]) { StopReason = StopReason.EndTurn }));
        return stream;
    }
}
