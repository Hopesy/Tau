// 作者：xxx
using System.Text.Json;
using Tau.Ai.Serialization;

namespace Tau.Ai.Providers.PiMessages;

/// <summary>【PiMessages】【会话报文】显式映射原生消息字段，避免基类序列化丢失正文和声明。</summary>
internal static class PiMessagesContextSerializer
{
    /// <summary>【PiMessages】【会话报文】规范化旧顶层字段，保留全部消息和系统增量的原始顺序。</summary>
    /// <param name="context">旧式或消息式上下文。</param>
    /// <param name="model">为旧助手消息补齐缺失来源的当前模型。</param>
    /// <returns>仅含 messages 的网关上下文对象。</returns>
    internal static Dictionary<string, object> Convert(LlmContext context, Model model)
    {
        // 1. 【PiMessages】【会话报文】网关负责声明重放，客户端只把旧字段转换为开场消息
        context = Transcript.NormalizeContext(context);
        var messages = new List<object>();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in context.Messages)
        {
            // 2. 【PiMessages】【工具结果】保留原调用标识，为旧结果补齐历史中的工具名称
            if (message is AssistantMessage assistant)
                foreach (var call in assistant.Content.OfType<ToolCallContent>()) toolNames[call.Id] = call.Name;
            messages.Add(message switch
            {
                SystemMessage system => SystemMessageJson.ToElement(system),
                UserMessage user => Fields(("role", "user"), ("content", Content(user.Content)),
                    ("timestamp", (user.Timestamp ?? DateTimeOffset.UnixEpoch).ToUnixTimeMilliseconds())),
                AssistantMessage response => Assistant(response, model),
                ToolResultMessage result => Fields(("role", "toolResult"), ("toolCallId", result.ToolCallId),
                    ("toolName", result.ToolName ?? toolNames.GetValueOrDefault(result.ToolCallId) ?? ""),
                    ("content", Content(result.Content)), ("isError", result.IsError), ("details", result.Details),
                    ("usage", result.Usage is { } usage ? Usage(usage) : null),
                    ("timestamp", (result.Timestamp ?? DateTimeOffset.UnixEpoch).ToUnixTimeMilliseconds())),
                _ => throw new NotSupportedException($"Unsupported pi-messages role '{message.Role}'. Convert custom messages before sending.")
            });
        }
        return new() { ["messages"] = messages };
    }

    /// <summary>【PiMessages】【助手报文】保存正文、来源、终态、用量和可选诊断信息。</summary>
    /// <param name="message">助手历史消息。</param>
    /// <param name="model">旧消息缺少来源字段时使用的当前模型。</param>
    /// <returns>网关助手消息。</returns>
    private static Dictionary<string, object> Assistant(AssistantMessage message, Model model) => Fields(
        ("role", "assistant"), ("content", Content(message.Content)),
        ("api", message.Api ?? model.Api), ("provider", message.Provider ?? model.Provider), ("model", message.Model ?? model.Id),
        ("responseId", message.ResponseId), ("responseModel", message.ResponseModel),
        ("providerThinkingLevel", message.ProviderThinkingLevel),
        ("thinkingLevel", message.ThinkingLevel),
        ("usage", Usage(message.Usage ?? new Usage(0, 0))), ("stopReason", StopReasonText(message.StopReason)),
        ("errorMessage", message.ErrorMessage), ("rawStopReason", message.RawStopReason), ("endTurn", message.EndTurn),
        ("deferred", message.Deferred is { } deferred ? Fields(("provider", deferred.Provider), ("modelId", deferred.ModelId),
            ("api", deferred.Api), ("id", deferred.Id), ("expiresAt", deferred.ExpiresAt?.ToUnixTimeMilliseconds()),
            ("pollAfterMs", deferred.PollAfterMs), ("data", deferred.Data)) : null),
        ("diagnostics", message.Diagnostics?.Select(diagnostic => Fields(("type", diagnostic.Type),
            ("timestamp", diagnostic.Timestamp.ToUnixTimeMilliseconds()), ("error", diagnostic.Error), ("details", diagnostic.Details))).ToList()),
        ("timestamp", (message.Timestamp ?? DateTimeOffset.UnixEpoch).ToUnixTimeMilliseconds()));

    /// <summary>【PiMessages】【内容报文】映射文本、图片、推理和工具调用，签名保持不透明原值。</summary>
    /// <param name="blocks">源内容块。</param>
    /// <returns>原顺序的 wire 内容块。</returns>
    private static List<Dictionary<string, object>> Content(IReadOnlyList<ContentBlock> blocks) => blocks.Select(block => block switch
    {
        TextContent text => Fields(("type", "text"), ("text", text.Text), ("textSignature", text.TextSignature)),
        ThinkingContent thinking => Fields(("type", "thinking"), ("thinking", thinking.Thinking),
            ("thinkingSignature", thinking.ThinkingSignature), ("redacted", thinking.Redacted ? true : null)),
        ImageContent image => Fields(("type", "image"), ("data", image.Data), ("mimeType", image.MimeType)),
        ToolCallContent call => Fields(("type", "toolCall"), ("id", call.Id), ("name", call.Name),
            ("arguments", Arguments(call.Arguments)), ("thoughtSignature", call.ThoughtSignature), ("namespace", call.Namespace)),
        _ => throw new NotSupportedException($"Unsupported pi-messages content '{block.Type}'.")
    }).ToList();

    /// <summary>【PiMessages】【工具参数】把 Tau 保存的 JSON 文本转换为独立 JSON 对象，避免二次字符串编码。</summary>
    /// <param name="arguments">完整工具调用参数 JSON。</param>
    /// <returns>网关要求的参数对象；损坏或非对象参数抛出异常。</returns>
    private static JsonElement Arguments(string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("pi-messages tool arguments must be a JSON object.");
        return document.RootElement.Clone();
    }

    /// <summary>【PiMessages】【用量报文】使用上游 token 和计费字段名，旧记录缺失项按零补齐。</summary>
    /// <param name="usage">Tau 用量。</param>
    /// <returns>网关用量对象。</returns>
    private static Dictionary<string, object> Usage(Usage usage)
    {
        var cost = usage.Cost ?? new UsageCost(0, 0);
        return Fields(("input", usage.InputTokens), ("output", usage.OutputTokens),
            ("cacheRead", usage.CacheReadTokens ?? 0), ("cacheWrite", usage.CacheWriteTokens ?? 0),
            ("cacheWrite1h", usage.CacheWrite1hTokens), ("reasoning", usage.ReasoningTokens),
            ("totalTokens", usage.TotalTokens ?? usage.InputTokens + usage.OutputTokens + (usage.CacheReadTokens ?? 0) + (usage.CacheWriteTokens ?? 0)),
            ("cost", Fields(("input", cost.Input), ("output", cost.Output), ("cacheRead", cost.CacheRead),
                ("cacheWrite", cost.CacheWrite), ("total", cost.Total))));
    }

    /// <summary>【PiMessages】【终态报文】将 Tau 枚举转换为上游字符串；内容过滤作为错误终态。</summary>
    /// <param name="reason">助手停止原因；空值表示仍未完成。</param>
    /// <returns>网关停止原因。</returns>
    private static string StopReasonText(StopReason? reason) => reason switch
    {
        StopReason.EndTurn => "stop", StopReason.MaxTokens => "length", StopReason.ToolUse => "toolUse",
        StopReason.Aborted => "aborted", StopReason.Deferred => "deferred", null => "pending", _ => "error"
    };

    /// <summary>【PiMessages】【报文组装】省略不存在的可选字段，保留零、false 和空字符串。</summary>
    /// <param name="fields">按协议顺序提供的字段。</param>
    /// <returns>仅包含有值字段的对象；嵌套 JSON 的 null 值不受影响。</returns>
    private static Dictionary<string, object> Fields(params (string Name, object? Value)[] fields) =>
        fields.Where(field => field.Value is not null).ToDictionary(field => field.Name, field => field.Value!, StringComparer.Ordinal);
}
