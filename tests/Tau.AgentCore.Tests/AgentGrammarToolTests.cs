// 作者：xxx
using System.Net;
using System.Text;
using System.Text.Json;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAi;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.AgentCore.Tests;

/// <summary>【AgentCore】【语法工具闭环】验证真实 Responses 和 Chat Completions 参数可被 Agent 执行并回传。</summary>
public sealed class AgentGrammarToolTests
{
    /// <summary>【AgentCore】【Responses 终态】完整工具项仍须服从响应终态，过滤或失败不得进入执行。</summary>
    /// <param name="status">响应状态。</param>
    /// <param name="reason">不完整原因。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("incomplete", "content_filter")]
    [InlineData("incomplete", null)]
    [InlineData("incomplete", "max_output_tokens")]
    [InlineData("cancelled", null)]
    [InlineData("failed", null)]
    public async Task Responses_RunRejectsFilteredAndFailedCalls(string status, string? reason)
    {
        using var handler = new GrammarHandler(true, status, reason);
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(client);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var tool = new GrammarTool();
        var runtime = new AgentRuntime();
        var config = new AgentLoopConfig
        {
            Model = new() { Id = "grammar", Name = "Grammar", Provider = "openai", Api = provider.Api, Compat = new() { SupportsOpenAiGrammarTools = true } },
            ProviderRegistry = registry, Tools = [tool], StreamOptions = new() { ApiKey = "test-key" }, InitialMessages = [new UserMessage("execute")]
        };
        var events = new List<AgentEvent>();
        await foreach (var item in runtime.RunAsync(config)) events.Add(item);
        Assert.Equal(0, tool.ExecutionCount);
        if (reason == "max_output_tokens")
        {
            Assert.Equal(2, handler.Bodies.Count);
            Assert.True(Assert.Single(events.OfType<ToolExecutionEndEvent>()).IsError);
            Assert.Null(runtime.State.ErrorMessage);
        }
        else
        {
            Assert.Single(handler.Bodies);
            Assert.Empty(events.OfType<ToolExecutionStartEvent>());
            Assert.Empty(events.OfType<ToolExecutionEndEvent>());
            Assert.NotNull(runtime.State.ErrorMessage);
        }
        var response = runtime.State.Messages.OfType<AssistantMessage>().First();
        Assert.Equal(reason == "max_output_tokens" ? StopReason.MaxTokens : StopReason.Error, response.StopReason);
        Assert.Equal(reason is null ? status : status + "." + reason, response.RawStopReason);
    }

    /// <summary>【AgentCore】【Chat 语法工具】完整调用执行并回传，缺失结束原因、截断和服务端错误不得执行。</summary>
    /// <param name="finish">结束原因，空值表示断流，error 表示服务端错误事件。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData("tool_calls")]
    [InlineData("length")]
    [InlineData("error")]
    [InlineData(null)]
    public async Task Chat_RunExecutesOnlySuccessfulGrammarCalls(string? finish)
    {
        using var handler = new ChatGrammarHandler(finish);
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var tool = new GrammarTool();
        var runtime = new AgentRuntime();
        var config = new AgentLoopConfig
        {
            Model = new() { Id = "grammar", Name = "Grammar", Provider = "openai", Api = provider.Api, Compat = new() { SupportsOpenAiGrammarTools = true, SupportsFinishReason = true } },
            ProviderRegistry = registry, Tools = [tool], StreamOptions = new() { ApiKey = "test-key" }, InitialMessages = [new UserMessage("execute")]
        };
        var events = new List<AgentEvent>();
        await foreach (var item in runtime.RunAsync(config)) events.Add(item);
        Assert.Equal("custom", handler.Bodies[0].GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal(finish == "tool_calls" ? 1 : 0, tool.ExecutionCount);
        if (finish is null or "error")
        {
            Assert.Single(handler.Bodies);
            Assert.Empty(events.OfType<ToolExecutionStartEvent>());
            Assert.Contains(finish is null ? "without finish_reason" : "provider failure", runtime.State.ErrorMessage);
            return;
        }
        Assert.Null(runtime.State.ErrorMessage);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(finish == "length", Assert.Single(events.OfType<ToolExecutionEndEvent>()).IsError);
        if (finish == "tool_calls") Assert.Equal("中文\nprint(1)", tool.ReceivedCode);
        var messages = handler.Bodies[1].GetProperty("messages").EnumerateArray().ToArray();
        var call = Assert.Single(messages, item => item.GetProperty("role").GetString() == "assistant").GetProperty("tool_calls")[0];
        var result = Assert.Single(messages, item => item.GetProperty("role").GetString() == "tool");
        Assert.Equal("custom", call.GetProperty("type").GetString());
        Assert.Equal("中文\nprint(1)", call.GetProperty("custom").GetProperty("input").GetString());
        Assert.Equal(call.GetProperty("id").GetString(), result.GetProperty("tool_call_id").GetString());
        Assert.Equal("done", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<AssistantMessage>(runtime.State.Messages.Last()).Content)).Text);
    }

    /// <summary>完整调用执行一次并进入下一轮，未完成调用返回错误且不得执行。</summary>
    /// <param name="complete">供应商是否发送工具项完成事件。</param>
    /// <returns>异步回归任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_ExecutesOnlyCompletedGrammarCalls(bool complete)
    {
        using var handler = new GrammarHandler(complete);
        using var client = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(client);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var tool = new GrammarTool();
        var runtime = new AgentRuntime();
        var config = new AgentLoopConfig
        {
            Model = new() { Id = "grammar", Name = "Grammar", Provider = "openai", Api = provider.Api, Compat = new() { SupportsOpenAiGrammarTools = true } },
            ProviderRegistry = registry, Tools = [tool], StreamOptions = new() { ApiKey = "test-key" },
            InitialMessages = [new UserMessage("execute")]
        };
        var events = new List<AgentEvent>();
        await foreach (var item in runtime.RunAsync(config)) events.Add(item);
        Assert.Equal("custom", handler.Bodies[0].GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal(complete ? 1 : 0, tool.ExecutionCount);
        if (!complete)
        {
            Assert.Single(handler.Bodies);
            Assert.Empty(events.OfType<ToolExecutionStartEvent>());
            Assert.Contains("unfinished tool call", runtime.State.ErrorMessage);
            return;
        }
        Assert.Null(runtime.State.ErrorMessage);
        Assert.Equal("中文\nprint(1)", tool.ReceivedCode);
        Assert.Single(events.OfType<ToolExecutionEndEvent>());
        Assert.Equal(2, handler.Bodies.Count);
        var input = handler.Bodies[1].GetProperty("input").EnumerateArray().ToArray();
        var call = Assert.Single(input, item => item.TryGetProperty("type", out var type) && type.GetString() == "custom_tool_call");
        var result = Assert.Single(input, item => item.TryGetProperty("type", out var type) && type.GetString() == "custom_tool_call_output");
        Assert.Equal("ctc_1", call.GetProperty("id").GetString());
        Assert.Equal(call.GetProperty("call_id").GetString(), result.GetProperty("call_id").GetString());
        Assert.Equal("done", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<AssistantMessage>(runtime.State.Messages.Last()).Content)).Text);
    }

    private sealed class GrammarTool : IAgentTool
    {
        public string Name => "execute";
        public string Label => "Execute";
        public string Description => "Execute test code";
        public int ExecutionCount { get; private set; }
        public string? ReceivedCode { get; private set; }
        public JsonElement ParameterSchema { get; } = Json("""{"type":"object","properties":{"code":{"type":"string"}},"required":["code"]}""");
        public ConstrainedSamplingConfig? ConstrainedSampling => new() { Type = "grammar", Variants = new Dictionary<string, string> { ["openai_lark"] = "start: WORD" } };

        /// <summary>记录准备完成后的输入并返回模拟执行结果。</summary>
        /// <param name="toolCallId">调用标识。</param>
        /// <param name="args">经过 Agent 参数校验的 JSON。</param>
        /// <param name="ct">取消信号。</param>
        /// <param name="onUpdate">可选进度回调。</param>
        /// <returns>成功工具结果。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
        {
            ExecutionCount++;
            ReceivedCode = args.GetProperty("code").GetString();
            return Task.FromResult(new ToolResult([new TextContent("executed")]));
        }
    }

    /// <summary>为两轮请求提供工具调用和最终回答。</summary>
    /// <param name="complete">是否完成工具调用。</param>
    /// <param name="status">首轮响应的终态。</param>
    /// <param name="reason">首轮不完整原因。</param>
    private sealed class GrammarHandler(bool complete, string status = "completed", string? reason = null) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        /// <summary>记录请求并生成真实 SSE 格式响应。</summary>
        /// <param name="request">实际 Provider 请求。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>预设的 SSE 响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(Json(await request.Content!.ReadAsStringAsync(cancellationToken)));
            var output = Bodies.Count == 1 ? new List<string>
            {
                """{"type":"response.output_item.added","output_index":0,"item":{"type":"custom_tool_call","id":"ctc_1","call_id":"call_1","name":"execute","input":""}}""",
                """{"type":"response.custom_tool_call_input.delta","output_index":0,"delta":"中文\nprint(1)"}"""
            } : ["""{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg_1","content":[{"type":"output_text","text":"done"}]}}"""];
            if (Bodies.Count == 1 && complete) output.Add("""{"type":"response.output_item.done","output_index":0,"item":{"type":"custom_tool_call","id":"ctc_1","call_id":"call_1","name":"execute","input":"中文\nprint(1)"}}""");
            var terminalStatus = Bodies.Count == 1 ? status : "completed";
            var details = Bodies.Count == 1 && reason is not null ? ",\"incomplete_details\":{\"reason\":\"" + reason + "\"}" : "";
            output.Add("{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"" + terminalStatus + "\"" + details + "}}");
            return new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(output.Select(item => "data: " + item + "\n\n")), Encoding.UTF8, "text/event-stream") };
        }
    }

    /// <summary>提供 Chat Completions 两轮协议响应。</summary>
    /// <param name="finish">首轮结束原因或错误类型。</param>
    private sealed class ChatGrammarHandler(string? finish) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        /// <summary>记录真实请求，并按场景返回完整调用、错误或截断。</summary>
        /// <param name="request">Provider 请求。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>SSE 响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(Json(await request.Content!.ReadAsStringAsync(cancellationToken)));
            var output = Bodies.Count == 1 ? new List<string>
            {
                """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","custom":{"name":"execute","input":"中文\n"}}]}}]}""",
                """{"choices":[{"delta":{"tool_calls":[{"index":0,"custom":{"input":"print(1)"}}]}}]}"""
            } : ["""{"choices":[{"delta":{"content":"done"},"finish_reason":"stop"}]}"""];
            if (Bodies.Count == 1 && finish is not null)
                output.Add(finish == "error" ? """{"error":{"message":"provider failure"}}""" :
                    "{\"choices\":[{\"delta\":{},\"finish_reason\":\"" + finish + "\"}]}");
            output.Add("[DONE]");
            return new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(output.Select(item => "data: " + item + "\n\n")), Encoding.UTF8, "text/event-stream") };
        }
    }

    /// <summary>解析独立持有内存的 JSON。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>JSON 副本。</returns>
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
