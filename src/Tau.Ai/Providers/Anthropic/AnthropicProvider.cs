using System.Text;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Streaming;
using Tau.Ai.Utilities;

namespace Tau.Ai.Providers.Anthropic;

/// <summary>
/// Anthropic Messages API streaming provider.
/// Endpoint: POST /v1/messages with "stream": true.
/// </summary>
public sealed class AnthropicProvider : IStreamProvider
{
    private const string DefaultAnthropicVersion = "2023-06-01";
    private const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";
    private const string FineGrainedToolStreamingBeta = "fine-grained-tool-streaming-2025-05-14";
    private const string InlineToolsBeta = "inline-tools-2026-09-15";
    private readonly HttpClient _httpClient;

    public AnthropicProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? TauHttpClientFactory.Create();
    }

    public string Api => "anthropic-messages";

    public bool SupportsTranscriptContext => true;

    /// <summary>【AI】【Anthropic 请求】使用协议专用选项启动流式请求。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">会话上下文。</param>
    /// <param name="options">协议和传输选项。</param>
    /// <returns>助手事件流。</returns>
    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) => StartStream(model, context, options, false);

    /// <summary>【AI】【Anthropic 请求】将通用思考设置映射到供应商选项后启动请求。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">会话上下文。</param>
    /// <param name="options">通用生成选项。</param>
    /// <returns>助手事件流。</returns>
    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => StartStream(model, context, options, true);

    /// <summary>【AI】【Anthropic 请求】统一创建响应元数据，确保请求前失败也保留本轮供应商等级。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">会话上下文。</param>
    /// <param name="options">生成选项。</param>
    /// <param name="simple">是否按通用思考选项处理。</param>
    /// <returns>异步填充的事件流。</returns>
    private AssistantMessageStream StartStream(Model model, LlmContext context, StreamOptions options, bool simple)
    {
        options = StreamOptionHelpers.WithCacheDefaults(options);
        var stream = new AssistantMessageStream();
        var reasoning = simple ? ((SimpleStreamOptions)options).Reasoning : null;
        var initial = new AssistantMessage
        {
            Api = Api, Provider = model.Provider, Model = model.Id, Timestamp = DateTimeOffset.UtcNow,
            ProviderThinkingLevel = model.Compat?.SupportsMidConvoEffort == true ? GetActiveEffort(model, options, reasoning) : null
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await StreamInternalAsync(model, context, options, stream, reasoning, simple, initial).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (options.Signal.IsCancellationRequested)
            {
                stream.Push(new ErrorEvent(StreamOptionHelpers.AbortedErrorMessage, Message: initial with { StopReason = StopReason.Aborted, ErrorMessage = StreamOptionHelpers.AbortedErrorMessage }));
            }
            catch (Exception ex)
            {
                stream.Push(new ErrorEvent(ex.Message, Message: initial with { StopReason = StopReason.Error, ErrorMessage = ex.Message }));
            }
        });

        return stream;
    }

    /// <summary>【AI】【Anthropic 请求】规范化会话，发送报文并将 SSE 转换为助手事件。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="context">旧式或消息式上下文。</param>
    /// <param name="options">认证、传输和生成选项。</param>
    /// <param name="stream">事件输出流。</param>
    /// <param name="reasoning">简化入口传入的思考级别。</param>
    /// <param name="simple">是否使用简化入口。</param>
    /// <param name="initial">请求开始前创建的响应元数据。</param>
    /// <returns>请求与事件转发任务。</returns>
    private async Task StreamInternalAsync(
        Model model,
        LlmContext context,
        StreamOptions options,
        AssistantMessageStream stream,
        ThinkingLevel? reasoning,
        bool simple,
        AssistantMessage initial)
    {
        options.Signal.ThrowIfCancellationRequested();

        var baseUrl = model.BaseUrl?.TrimEnd('/') ?? "https://api.anthropic.com";
        var url = $"{baseUrl}/v1/messages";
        var authToken = ProviderEnvironment.GetValue("ANTHROPIC_AUTH_TOKEN", options.Env);
        var apiKey = options.ApiKey ?? authToken ?? ProviderEnvironment.GetValue("ANTHROPIC_OAUTH_TOKEN", options.Env) ?? ProviderEnvironment.GetValue("ANTHROPIC_API_KEY", options.Env);
        var isOAuth = AnthropicProtocol.IsOAuthToken(apiKey, model.Provider);

        // 1. 【AI】【Anthropic 预算】简化入口先按原始会话计算输出与思考空间
        (int MaxTokens, int? ThinkingBudget)? tokenBudget = simple
            ? SimpleTokenOptions.ResolveAnthropic(model, context, options.MaxTokens,
                reasoning is not null and not ThinkingLevel.Off && !UsesAdaptiveThinking(model)
                    ? StreamOptionHelpers.GetThinkingBudget(((SimpleStreamOptions)options).ThinkingBudgets, reasoning.Value, 1_024, 2_048, 8_192, 16_384)
                    : null)
            : null;

        // 2. 【AI】【Anthropic 会话】正文和 beta 请求头共用同一份按模型能力处理的声明
        context = Transcript.ResolveTranscript(context, model.Compat?.SupportsMidConvoSystemMessages == true);
        var body = await StreamOptionHelpers.ApplyPayloadCallbackAsync(
            options,
            model,
            BuildRequestBody(model, context, options, reasoning, simple, isOAuth, tokenBudget)).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, AnthropicRequestJsonContext.Default.DictionaryStringObject);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var isGitHubCopilot = model.Provider.Equals("github-copilot", StringComparison.OrdinalIgnoreCase);
        if ((isGitHubCopilot || isOAuth) && !string.IsNullOrWhiteSpace(apiKey) && !EnvironmentApiKeyResolver.IsAuthenticatedMarker(apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }
        else if (!string.IsNullOrEmpty(apiKey) && !string.Equals(apiKey, authToken, StringComparison.Ordinal))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }
        else if (!string.IsNullOrEmpty(authToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
        }

        request.Headers.TryAddWithoutValidation("anthropic-version", DefaultAnthropicVersion);
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
        if (isOAuth)
        {
            request.Headers.TryAddWithoutValidation("user-agent", $"claude-cli/{AnthropicProtocol.ClaudeCodeVersion}");
            request.Headers.TryAddWithoutValidation("x-app", "cli");
        }

        if (isGitHubCopilot)
        {
            foreach (var (key, value) in GitHubCopilotHeaders.BuildDynamicHeaders(context.Messages))
            {
                request.Headers.Remove(key);
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        if (!isOAuth && !isGitHubCopilot) SessionAffinityHeaders.Apply(request, model, options, "anthropic-messages");
        ApplyHeaders(request, model.Headers);
        ApplyHeaders(request, options.Headers);
        request.Headers.Remove("anthropic-beta");
        if (BuildAnthropicBetaHeader(model, context, options, reasoning, simple, isOAuth) is { } betaHeader)
            request.Headers.TryAddWithoutValidation("anthropic-beta", betaHeader);

        using var requestTimeout = StreamOptionHelpers.CreateRequestTimeout(options);
        try
        {
            await StreamOptionHelpers.ApplyHeadersCallbackAsync(options, model, request).ConfigureAwait(false);
            using var response = await ProviderHttpRetry.SendAsync(_httpClient, request, model, options, requestTimeout.Token,
                retryTransportErrors: true).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
                throw new HttpRequestException($"Anthropic API error {(int)response.StatusCode}: {errorBody}", null, response.StatusCode);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);

            var currentTools = Transcript.GetCurrentTools(context.Messages);
            var parser = new AnthropicStreamParser(initial, stream, isOAuth ? name => AnthropicProtocol.FromClaudeCodeName(name, currentTools) : null);

            await foreach (var sse in SseParser.ParseAsync(responseStream, requestTimeout.Token))
            {
                if (string.IsNullOrEmpty(sse.Data))
                    continue;
                await StreamOptionHelpers.InvokeProviderStreamEventAsync(options, model, sse.Data).ConfigureAwait(false);
                if (parser.ParseEvent(sse.EventType, sse.Data))
                    break;
            }
        }
        catch (OperationCanceledException ex) when (requestTimeout.IsTimeoutCancellation)
        {
            throw requestTimeout.CreateTimeoutException(ex);
        }
    }

    /// <summary>【AI】【Anthropic 会话】组装开场提示、原生系统更新与工具增删请求。</summary>
    /// <param name="model">目标模型及兼容能力。</param>
    /// <param name="context">已按模型能力处理的消息式上下文。</param>
    /// <param name="options">生成和缓存选项。</param>
    /// <param name="reasoning">简化入口的思考级别。</param>
    /// <param name="simple">是否使用简化入口。</param>
    /// <param name="isOAuth">是否使用 OAuth 协议身份和工具名称。</param>
    /// <param name="tokenBudget">简化入口计算后的预算；高级入口为空。</param>
    /// <returns>Anthropic 请求报文。</returns>
    private static Dictionary<string, object> BuildRequestBody(
        Model model,
        LlmContext context,
        StreamOptions options,
        ThinkingLevel? reasoning,
        bool simple,
        bool isOAuth,
        (int MaxTokens, int? ThinkingBudget)? tokenBudget)
    {
        context = MessageTransformer.DowngradeUnsupportedImages(context, model);
        var cacheControl = BuildCacheControl(model, options);
        var initial = context.Messages.Count > 0 ? context.Messages[0] as SystemMessage : null;
        var nativeToolChanges = UsesNativeToolChanges(model, context);
        var eagerStreaming = model.Compat?.SupportsEagerToolInputStreaming ?? true;
        var strictTools = model.Compat?.SupportsStrictTools ?? false;
        var body = new Dictionary<string, object>
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["max_tokens"] = tokenBudget?.MaxTokens ?? options.MaxTokens ?? model.MaxOutputTokens ?? 4096
        };

        // 1. 【AI】【Anthropic 会话】仅开场声明进入顶层 system，后续声明由消息转换器安排位置
        var systemPrompt = initial is null ? "" : Transcript.GetSystemMessageText(initial);
        if (systemPrompt.Length > 0 || isOAuth)
        {
            body["system"] = BuildSystemPrompt(systemPrompt, cacheControl, isOAuth);
        }

        // 2. 【AI】【Anthropic 重放】过滤中断历史、隔离跨模型签名并统一配对工具调用与结果
        var transformed = MessageTransformer.TransformMessages(context.Messages, model,
            static (id, _, _) => AnthropicMessageConverter.NormalizeToolCallId(id));
        body["messages"] = AnthropicMessageConverter.ConvertMessages(
            initial is null ? transformed : transformed.Skip(1).ToArray(),
            cacheControl,
            allowEmptySignature: model.Compat?.AllowEmptySignature == true,
            convertToolDefinitions: nativeToolChanges
                ? tools => AnthropicMessageConverter.ConvertTools(tools, eagerStreaming, supportsStrictTools: strictTools, isOAuth: isOAuth)
                : null,
            isOAuth: isOAuth,
            managedProvider: model.Compat?.SupportsMidConvoEffort == true ? model.Provider : null,
            activeEffort: GetActiveEffort(model, options, reasoning));

        // 3. 【AI】【Anthropic 工具】固定开场定义和占位工具，后续新增、移除及重定义留在消息中
        var requestTools = nativeToolChanges ? initial!.ToolsAdded! : Transcript.GetCurrentTools(context.Messages);
        if (requestTools.Count > 0)
        {
            var toolCacheControl = model.Compat?.SupportsCacheControlOnTools == false ? null : cacheControl;
            var tools = AnthropicMessageConverter.ConvertTools(
                requestTools,
                supportsEagerToolInputStreaming: eagerStreaming,
                cacheControl: toolCacheControl,
                supportsStrictTools: strictTools,
                isOAuth: isOAuth);
            if (nativeToolChanges) tools.Add(CreateDeferredToolPlaceholder());
            body["tools"] = tools;
        }

        var thinking = BuildThinking(model, options, reasoning, simple, tokenBudget?.ThinkingBudget);

        if (options.Temperature.HasValue && !ThinkingIsEnabled(thinking) && model.Compat?.SupportsTemperature != false)
            body["temperature"] = options.Temperature.Value;
        if (options.TopP.HasValue)
            body["top_p"] = options.TopP.Value;

        if (thinking is not null)
            body["thinking"] = thinking;

        if (model.Compat?.AllowedFallbackModels is { Count: > 0 } fallbacks)
        {
            body["fallbacks"] = fallbacks
                .Select(fallback => (object)new Dictionary<string, object> { ["model"] = fallback.Model })
                .ToArray();
        }

        if (model.Compat?.SupportsMidConvoEffort == true)
            body["output_config"] = new Dictionary<string, object> { ["effort"] = "high" };
        else if (thinking is not null &&
            IsAdaptiveThinking(thinking) &&
            reasoning is { } simpleReasoning)
        {
            body["output_config"] = new Dictionary<string, object>
            {
                ["effort"] = AnthropicProtocol.MapEffort(model, simpleReasoning)
            };
        }

        if (options is AnthropicOptions anthropicOptions)
        {
            if (model.Compat?.SupportsMidConvoEffort != true && !string.IsNullOrWhiteSpace(anthropicOptions.Effort) &&
                thinking is not null &&
                IsAdaptiveThinking(thinking))
                body["output_config"] = new Dictionary<string, object> { ["effort"] = anthropicOptions.Effort! };

            if (anthropicOptions.ToolChoice is { } toolChoice)
                body["tool_choice"] = MapToolChoice(toolChoice);
        }
        else if (simple && options is SimpleStreamOptions { ToolChoice: { } choice })
        {
            body["tool_choice"] = choice is string kind ? new Dictionary<string, object> { ["type"] = kind } : choice;
        }

        if (TryGetMetadataString(options.Metadata, "user_id", out var userId))
            body["metadata"] = new Dictionary<string, object> { ["user_id"] = userId! };

        return body;
    }

    /// <summary>【AI】【Anthropic OAuth】构造系统块，OAuth 身份始终为独立首块，每块使用相同缓存设置。</summary>
    /// <param name="systemPrompt">开场系统指令。</param>
    /// <param name="cacheControl">缓存设置。</param>
    /// <param name="isOAuth">是否插入协议身份。</param>
    /// <returns>系统内容块。</returns>
    private static object BuildSystemPrompt(
        string systemPrompt,
        IReadOnlyDictionary<string, object>? cacheControl,
        bool isOAuth)
    {
        var blocks = new List<object>();
        foreach (var text in (isOAuth ? new[] { AnthropicProtocol.ClaudeCodeIdentity, systemPrompt } : new[] { systemPrompt }).Where(text => text.Length > 0))
        {
            var block = new Dictionary<string, object> { ["type"] = "text", ["text"] = UnicodeTextSanitizer.RemoveUnpairedSurrogates(text) };
            if (cacheControl is not null) block["cache_control"] = cacheControl;
            blocks.Add(block);
        }
        return blocks;
    }

    private static IReadOnlyDictionary<string, object>? BuildCacheControl(Model model, StreamOptions options)
    {
        if (options.CacheRetention == CacheRetention.None)
        {
            return null;
        }

        var cacheControl = new Dictionary<string, object>
        {
            ["type"] = "ephemeral"
        };
        if (options.CacheRetention == CacheRetention.Long &&
            model.Compat?.SupportsLongCacheRetention != false)
        {
            cacheControl["ttl"] = "1h";
        }

        return cacheControl;
    }

    private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.Remove(key);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    /// <summary>【AI】【Anthropic 思考】构造能力驱动的思考配置，会话 effort 固定启用自适应及前缀失配丢弃。</summary>
    /// <param name="model">模型能力。</param>
    /// <param name="options">协议选项。</param>
    /// <param name="reasoning">通用思考等级。</param>
    /// <param name="simple">是否把缺省或 Off 当作关闭思考。</param>
    /// <param name="thinkingBudget">简化入口计算后的固定思考预算。</param>
    /// <returns>思考配置；无需发送时为空。</returns>
    private static Dictionary<string, object>? BuildThinking(
        Model model,
        StreamOptions options,
        ThinkingLevel? reasoning,
        bool simple,
        int? thinkingBudget)
    {
        if (model.Compat?.SupportsMidConvoEffort == true)
            return new Dictionary<string, object>
            {
                ["type"] = "adaptive", ["display"] = (options as AnthropicOptions)?.ThinkingDisplay ?? "summarized",
                ["block_binding"] = new Dictionary<string, object> { ["prefix_mismatch_behavior"] = "drop_block" }
            };
        if (!model.Reasoning)
            return null;

        if (simple && (reasoning is null or ThinkingLevel.Off)) return DisabledThinking(model);
        if (options is AnthropicOptions anthropicOptions)
        {
            if (anthropicOptions.ThinkingEnabled == false)
                return DisabledThinking(model);

            if (anthropicOptions.ThinkingEnabled == true)
            {
                var display = string.IsNullOrWhiteSpace(anthropicOptions.ThinkingDisplay)
                    ? "summarized"
                    : anthropicOptions.ThinkingDisplay!;

                if (UsesAdaptiveThinking(model))
                    return new Dictionary<string, object>
                    {
                        ["type"] = "adaptive",
                        ["display"] = display
                    };

                return new Dictionary<string, object>
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = anthropicOptions.ThinkingBudgetTokens is null or 0 ? 1_024 : anthropicOptions.ThinkingBudgetTokens.Value,
                    ["display"] = display
                };
            }
        }

        if (!reasoning.HasValue)
            return null;

        if (UsesAdaptiveThinking(model))
            return new Dictionary<string, object>
            {
                ["type"] = "adaptive",
                ["display"] = "summarized"
            };

        var budget = thinkingBudget ?? StreamOptionHelpers.GetThinkingBudget(
            (options as SimpleStreamOptions)?.ThinkingBudgets,
            reasoning.Value,
            defaultMinimal: 1_024,
            defaultLow: 2_048,
            defaultMedium: 8_192,
            defaultHigh: 16_384);
        return new Dictionary<string, object>
        {
            ["type"] = "enabled",
            // 1. 【AI】【Anthropic 预算】保持上游 buildParams 对零预算回退为 1024 的行为
            ["budget_tokens"] = budget == 0 ? 1_024 : budget,
            ["display"] = "summarized"
        };
    }

    private static bool ThinkingIsEnabled(Dictionary<string, object>? thinking) =>
        thinking is not null &&
        (!thinking.TryGetValue("type", out var type) ||
         !string.Equals(Convert.ToString(type), "disabled", StringComparison.Ordinal));

    private static bool IsAdaptiveThinking(Dictionary<string, object> thinking) =>
        thinking.TryGetValue("type", out var type) &&
        string.Equals(Convert.ToString(type), "adaptive", StringComparison.Ordinal);

    /// <summary>按模型的 off 映射和旧兼容标记决定是否允许发送 disabled。</summary>
    /// <param name="model">目标模型。</param>
    /// <returns>关闭思考配置；模型禁止时为空。</returns>
    private static Dictionary<string, object>? DisabledThinking(Model model) =>
        model.Compat?.SupportsDisabledThinking == false || (model.ThinkingLevelMap?.TryGetValue("off", out var off) == true && off is null)
            ? null : new Dictionary<string, object> { ["type"] = "disabled" };

    /// <summary>获取当前请求的原生 effort，未指定时使用 high。</summary>
    /// <param name="model">目标模型。</param>
    /// <param name="options">协议选项。</param>
    /// <param name="reasoning">简化入口思考等级。</param>
    /// <returns>本轮供应商等级。</returns>
    private static string GetActiveEffort(Model model, StreamOptions options, ThinkingLevel? reasoning)
    {
        if (options is AnthropicOptions { Effort: { } effort }) return effort;
        return UsesAdaptiveThinking(model) && reasoning is { } level && level != ThinkingLevel.Off ? AnthropicProtocol.MapEffort(model, level) : "high";
    }

    private static object MapToolChoice(AnthropicToolChoice choice)
    {
        if (choice.IsTool)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "tool",
                ["name"] = choice.Name!
            };
        }

        return new Dictionary<string, object>
        {
            ["type"] = choice.Kind
        };
    }

    /// <summary>【AI】【Anthropic 工具】判断是否具备原生工具变化所需的能力与开场工具。</summary>
    /// <param name="model">模型能力配置。</param>
    /// <param name="context">已规范化的上下文。</param>
    /// <returns>两项能力均启用且存在开场工具时返回 true。</returns>
    private static bool UsesNativeToolChanges(Model model, LlmContext context) =>
        model.Compat?.SupportsMidConvoSystemMessages == true && model.Compat.SupportsMidConvoToolChanges == true &&
        context.Messages.Count > 0 && context.Messages[0] is SystemMessage { ToolsAdded.Count: > 0 };

    /// <summary>【AI】【Anthropic 缓存】创建始终不激活的延迟工具占位项，稳定首次请求起的缓存前缀。</summary>
    /// <returns>不带缓存标记的独立占位工具定义。</returns>
    private static Dictionary<string, object> CreateDeferredToolPlaceholder() => new()
    {
        ["name"] = "__pi_deferred_placeholder__",
        ["description"] = "Reserved placeholder. Never available. Never call this.",
        ["input_schema"] = new Dictionary<string, object>
        {
            ["type"] = "object", ["properties"] = new Dictionary<string, object>(), ["required"] = new List<object>()
        },
        ["defer_loading"] = true
    };

    /// <summary>【AI】【Anthropic 协议】根据当前工具和原生变化能力生成默认 beta 请求头。</summary>
    /// <param name="model">模型能力。</param>
    /// <param name="context">已规范化的上下文。</param>
    /// <param name="options">传输及协议选项。</param>
    /// <param name="reasoning">简化入口思考等级。</param>
    /// <param name="simple">是否使用简化入口。</param>
    /// <param name="isOAuth">是否启用 OAuth 身份。</param>
    /// <returns>逗号分隔的 beta 特性；没有特性时返回 null。</returns>
    private static string? BuildAnthropicBetaHeader(Model model, LlmContext context, StreamOptions options, ThinkingLevel? reasoning, bool simple, bool isOAuth)
    {
        // 1. 【AI】【Anthropic 协议】显式请求头覆盖默认 beta，空值删除；请求级设置优先于模型级
        var configured = false;
        string? configuredFeatures = null;
        foreach (var headers in new[] { model.Headers, options.Headers })
            foreach (var pair in headers ?? new Dictionary<string, string>())
                if (pair.Key.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase)) { configured = true; configuredFeatures = pair.Value; }
        if (configured)
        {
            var features = (configuredFeatures ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal);
            var header = string.Join(",", features);
            return header.Length == 0 ? null : header;
        }

        // 2. 【AI】【Anthropic 协议】按实际能力和请求选项生成默认 beta
        var betaFeatures = new List<string>();
        if (isOAuth) betaFeatures.AddRange(["claude-code-20250219", "oauth-2025-04-20"]);

        if (Transcript.GetCurrentTools(context.Messages).Count > 0 &&
            model.Compat?.SupportsEagerToolInputStreaming == false)
        {
            betaFeatures.Add(FineGrainedToolStreamingBeta);
        }

        var anthropic = options as AnthropicOptions;
        var thinkingEnabled = simple ? reasoning is not null and not ThinkingLevel.Off : anthropic?.ThinkingEnabled == true;
        if (model.Reasoning && thinkingEnabled && anthropic?.InterleavedThinking != false && !UsesAdaptiveThinking(model))
        {
            betaFeatures.Add(InterleavedThinkingBeta);
        }

        if (model.Compat?.AllowedFallbackModels is { Count: > 0 })
        {
            betaFeatures.Add("server-side-fallback-2026-07-01");
        }

        if (UsesNativeToolChanges(model, context)) betaFeatures.Add(InlineToolsBeta);
        if (model.Compat?.SupportsMidConvoEffort == true)
            betaFeatures.AddRange(["mid-conversation-output-config-2026-07-01", "thinking-binding-controls-2026-08-01"]);

        return betaFeatures.Count == 0 ? null : string.Join(",", betaFeatures);
    }

    /// <summary>只按显式能力启用自适应思考，不从模型名称推断。</summary>
    /// <param name="model">目标模型。</param>
    /// <returns>声明支持自适应思考时为 true。</returns>
    private static bool UsesAdaptiveThinking(Model model) => model.Compat?.ForceAdaptiveThinking == true;

    private static bool TryGetMetadataString(
        IDictionary<string, object>? metadata,
        string key,
        out string? value)
    {
        value = null;
        if (metadata is null || !metadata.TryGetValue(key, out var raw))
            return false;

        value = raw switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            JsonElement { ValueKind: JsonValueKind.String } element when !string.IsNullOrWhiteSpace(element.GetString()) => element.GetString(),
            _ => null
        };

        return value is not null;
    }

}

public record AnthropicOptions : StreamOptions
{
    public bool? ThinkingEnabled { get; init; }
    public int? ThinkingBudgetTokens { get; init; }
    public string? Effort { get; init; }
    public string? ThinkingDisplay { get; init; }
    public bool? InterleavedThinking { get; init; }
    public AnthropicToolChoice? ToolChoice { get; init; }
}

public sealed record AnthropicToolChoice
{
    private AnthropicToolChoice(string kind, string? name)
    {
        Kind = kind;
        Name = name;
    }

    public string Kind { get; }
    public string? Name { get; }
    public bool IsTool => Name is not null;

    public static AnthropicToolChoice FromString(string choice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(choice);
        return new AnthropicToolChoice(choice, name: null);
    }

    public static AnthropicToolChoice Tool(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new AnthropicToolChoice("tool", name);
    }

    public static implicit operator AnthropicToolChoice(string choice) =>
        FromString(choice);

    public static implicit operator string?(AnthropicToolChoice? choice) =>
        choice?.ToString();

    public override string ToString() =>
        IsTool ? $"tool:{Name}" : Kind;
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(object))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(float))]
[System.Text.Json.Serialization.JsonSerializable(typeof(float?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal?))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan?))]
internal partial class AnthropicRequestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
