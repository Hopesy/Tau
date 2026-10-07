// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Streaming;

namespace Tau.ResponsesSmoke;

/// <summary>【AI】【Responses 实测】使用环境变量凭据验证真实流式接口和无副作用工具闭环。</summary>
internal static class Program
{
    /// <summary>执行显式选择的在线探测，仅打印脱敏后的检查结果。</summary>
    /// <param name="args">--live 必选；可选 text、reasoning、history、tool、limit 或 cancel 单项。</param>
    /// <returns>全部检查成功返回 0，配置或验证失败返回 1。</returns>
    private static async Task<int> Main(string[] args)
    {
        var key = Environment.GetEnvironmentVariable("TAU_SMOKE_API_KEY");
        if (args.Length is < 1 or > 2 || args[0] != "--live" || string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("【AI】【Responses 实测】需要 --live 与 TAU_SMOKE_API_KEY；可选 TAU_SMOKE_BASE_URL、TAU_SMOKE_MODEL");
            return 1;
        }

        try
        {
            // 1. 【AI】【Responses 实测】限制目的地址、请求次数与整个探测的运行时间
            var baseUrl = Environment.GetEnvironmentVariable("TAU_SMOKE_BASE_URL") ?? "https://api.deepseek.com";
            var uri = new Uri(baseUrl, UriKind.Absolute);
            Require(uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
                && string.IsNullOrEmpty(uri.Fragment), "Base URL must be HTTPS without credentials, query or fragment.");
            using var handler = new BudgetHandler(uri) { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } };
            using var client = new HttpClient(handler);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var provider = new OpenAiResponsesProvider(client);
            var model = new Model
            {
                Id = Environment.GetEnvironmentVariable("TAU_SMOKE_MODEL") ?? "deepseek-flash",
                Name = "Responses smoke", Provider = uri.Host, Api = provider.Api, BaseUrl = baseUrl,
                Reasoning = true, MaxOutputTokens = 1024,
                Compat = new() { SupportsDeveloperRole = false, SupportsStrictMode = false }
            };
            var options = new OpenAiResponsesOptions { ApiKey = key, MaxTokens = 512, ReasoningEffort = "none",
                Signal = deadline.Token, Timeout = TimeSpan.FromSeconds(45), MaxRetries = 0 };
            var cases = args.Length == 2 ? new[] { args[1] } : new[] { "text", "reasoning", "history", "tool", "limit", "cancel" };

            // 2. 【AI】【Responses 实测】逐项断言真实 Provider 的结果，失败立即停止后续计费请求
            foreach (var name in cases)
            {
                switch (name)
                {
                    case "text":
                    case "reasoning":
                        var reply = await ReadAsync(provider.StreamSimple(model, new(null, [new UserMessage("Reply with exactly TAU_OK.")], null),
                            new SimpleStreamOptions { ApiKey = key, MaxTokens = options.MaxTokens, Signal = options.Signal,
                                Timeout = options.Timeout, MaxRetries = 0, Reasoning = name == "reasoning" ? ThinkingLevel.Low : ThinkingLevel.Off }));
                        Require(reply.StopReason == StopReason.EndTurn && Text(reply).Contains("TAU_OK", StringComparison.Ordinal), Diagnostic(reply));
                        Require(reply.Usage is { OutputTokens: > 0 } && reply.ResponseId is not null && reply.ResponseModel is not null, "Missing usage, response ID or actual model.");
                        Require(name != "reasoning" || reply.Content.OfType<ThinkingContent>().Any(t => !string.IsNullOrEmpty(t.ThinkingSignature)), "Missing replayable reasoning.");
                        Require(name != "text" || !reply.Content.OfType<ThinkingContent>().Any(), "ThinkingLevel.Off did not disable reasoning.");
                        Report(name, reply);
                        break;
                    case "history":
                        var nonce = Guid.NewGuid().ToString("N");
                        var prompt = new UserMessage("Remember this token: " + nonce + ". Reply OK.");
                        var first = await ReadAsync(provider.Stream(model, new(null, [prompt], null), options));
                        Require(first.StopReason == StopReason.EndTurn, Diagnostic(first));
                        var second = await ReadAsync(provider.Stream(model, new(null,
                            [prompt, first, new UserMessage("Reply with the remembered token only.")], null), options));
                        Require(second.StopReason == StopReason.EndTurn && Text(second).Contains(nonce, StringComparison.Ordinal), Diagnostic(second));
                        Report(name, second);
                        break;
                    case "tool":
                        await CheckToolAsync(provider, model, options);
                        break;
                    case "limit":
                        var limited = await ReadAsync(provider.Stream(model, new(null,
                            [new UserMessage("List the integers from 1 to 1000, one per line.")], null), options with { MaxTokens = 8 }));
                        Require(limited.StopReason == StopReason.MaxTokens && limited.RawStopReason == "incomplete.max_output_tokens", Diagnostic(limited));
                        Report(name, limited);
                        break;
                    case "cancel":
                        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                        {
                            var cancelled = await ReadAsync(provider.Stream(model, new(null,
                                [new UserMessage("List the integers from 1 to 1000, one per line.")], null), options with { Signal = cancel.Token }), cancel);
                            Require(cancelled.StopReason == StopReason.Aborted && cancelled.Content.Count > 0 && cancelled.ResponseId is not null,
                                "Cancellation must preserve partial content and response ID. " + Diagnostic(cancelled));
                            Report(name, cancelled);
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Unknown smoke case.");
                }
            }
            Console.WriteLine($"【AI】【Responses 实测】PASS requests={handler.Requests}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("【AI】【Responses 实测】FAIL " + exception.Message.Replace(key, "[REDACTED]", StringComparison.Ordinal));
            return 1;
        }
    }

    /// <summary>读取完整事件流，取消场景在首个非空正文增量后发出取消信号。</summary>
    /// <param name="stream">真实 Provider 事件流。</param>
    /// <param name="cancel">可选取消源。</param>
    /// <returns>终态助手消息。</returns>
    private static async Task<AssistantMessage> ReadAsync(AssistantMessageStream stream, CancellationTokenSource? cancel = null)
    {
        var terminals = 0;
        await foreach (var item in stream)
        {
            if (item is TextDeltaEvent { Delta.Length: > 0 }) cancel?.Cancel();
            if (item is DoneEvent or ErrorEvent) terminals++;
        }
        Require(terminals == 1, "Expected exactly one terminal event.");
        return await stream.ResultAsync;
    }

    /// <summary>验证真实工具参数、Agent 执行、推理历史回传和最终答案。</summary>
    /// <param name="provider">待验证 Provider。</param>
    /// <param name="model">实测模型。</param>
    /// <param name="options">凭据、预算和取消配置。</param>
    /// <returns>工具闭环检查任务。</returns>
    private static async Task CheckToolAsync(OpenAiResponsesProvider provider, Model model, OpenAiResponsesOptions options)
    {
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, () => provider);
        var tool = new EchoTool();
        var simple = new SimpleStreamOptions { ApiKey = options.ApiKey, Signal = options.Signal, Timeout = options.Timeout,
            MaxTokens = 1024, MaxRetries = 0, Reasoning = ThinkingLevel.Low, ToolChoice = "auto" };
        var turns = 0;
        var agent = new Agent(new AgentOptions
        {
            Model = model, ProviderRegistry = registry, Tools = [tool], StreamOptions = simple,
            FinishTurnAsync = (_, _) => Task.FromResult<AgentTurnDecision?>(++turns >= 2 ? AgentTurnDecision.End : null)
        });
        await agent.PromptAsync("Call lookup_token with label tau once. Then reply with exactly the token returned by the tool.", cancellationToken: options.Signal);
        var replies = agent.State.Messages.OfType<AssistantMessage>().ToArray();
        Require(agent.State.ErrorMessage is null && replies.Length == 2 && tool.Calls == 1 && tool.Label == "tau",
            agent.State.ErrorMessage ?? "Tool execution or replay failed.");
        Require(replies[0].StopReason == StopReason.ToolUse && replies[0].Content.OfType<ThinkingContent>().Any(), "Missing tool reasoning.");
        Require(replies[1].StopReason == StopReason.EndTurn && Text(replies[1]).Contains(tool.Token, StringComparison.Ordinal), Diagnostic(replies[1]));
        Report("tool", replies[1]);
    }

    /// <summary>读取助手可见正文。</summary>
    /// <param name="message">助手消息。</param>
    /// <returns>合并后的正文。</returns>
    private static string Text(AssistantMessage message) => string.Concat(message.Content.OfType<TextContent>().Select(t => t.Text));

    /// <summary>生成不含正文、签名或请求凭据的终态诊断。</summary>
    /// <param name="message">助手消息。</param>
    /// <returns>停止原因及供应商错误。</returns>
    private static string Diagnostic(AssistantMessage message) => $"stop={message.StopReason}, raw={message.RawStopReason}, error={message.ErrorMessage}";

    /// <summary>打印检查状态和用量，不输出模型思考内容。</summary>
    /// <param name="name">场景名称。</param>
    /// <param name="message">终态消息。</param>
    private static void Report(string name, AssistantMessage message) => Console.WriteLine(
        $"【AI】【Responses 实测】PASS {name} stop={message.StopReason} input={message.Usage?.InputTokens} output={message.Usage?.OutputTokens} cacheRead={message.Usage?.CacheReadTokens} reasoning={message.Usage?.ReasoningTokens}");

    /// <summary>断言检查条件，不满足时停止探测。</summary>
    /// <param name="condition">检查条件。</param>
    /// <param name="message">失败诊断。</param>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>【AI】【Responses 实测】仅返回随机测试值，不执行模型生成的代码。</summary>
    private sealed class EchoTool : IAgentTool
    {
        public string Name => "lookup_token";
        public string Label { get; private set; } = "";
        public string Description => "Return the test token for the label tau.";
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public int Calls { get; private set; }
        public JsonElement ParameterSchema { get; } = CreateSchema();

        /// <summary>创建参数定义并释放 JSON 文档缓冲。</summary>
        /// <returns>独立持有的工具参数定义。</returns>
        private static JsonElement CreateSchema()
        {
            using var document = JsonDocument.Parse("""{"type":"object","properties":{"label":{"type":"string"}},"required":["label"],"additionalProperties":false}""");
            return document.RootElement.Clone();
        }

        /// <summary>记录参数并返回本轮随机值。</summary>
        /// <param name="toolCallId">调用标识。</param>
        /// <param name="args">校验后的参数。</param>
        /// <param name="ct">取消信号。</param>
        /// <param name="onUpdate">可选进度回调。</param>
        /// <returns>仅含测试值的工具结果。</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, JsonElement args, CancellationToken ct = default, Func<ToolUpdate, Task>? onUpdate = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Label = args.GetProperty("label").GetString() ?? "";
            return Task.FromResult(new ToolResult([new TextContent(Token)]));
        }
    }

    /// <summary>【AI】【Responses 实测】每次运行最多发送八次同源请求。</summary>
    /// <param name="origin">用户指定的目的地址。</param>
    private sealed class BudgetHandler(Uri origin) : DelegatingHandler
    {
        public int Requests { get; private set; }

        /// <summary>验证目的地址及请求预算后发送请求。</summary>
        /// <param name="request">待发送请求。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>服务器响应。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.RequestUri?.GetLeftPart(UriPartial.Authority) == origin.GetLeftPart(UriPartial.Authority), "Unexpected request destination.");
            Require(++Requests <= 8, "Live request budget exceeded.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
