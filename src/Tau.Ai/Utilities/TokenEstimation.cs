// 作者：xxx
using System.Globalization;
using System.Text.Json;
using Tau.Ai.Serialization;

namespace Tau.Ai;

/// <summary>【AI】【上下文估算】记录最近有效用量与其后消息的字符估算。</summary>
/// <param name="Tokens">上下文总 token 估值。</param>
/// <param name="UsageTokens">最近有效助手用量，没有时为零。</param>
/// <param name="TrailingTokens">有效用量之后的消息估值，没有用量时包含全部消息。</param>
/// <param name="LastUsageIndex">有效助手消息索引，没有时为空。</param>
public readonly record struct ContextUsageEstimate(long Tokens, long UsageTokens, long TrailingTokens, int? LastUsageIndex);

/// <summary>【AI】【上下文估算】按上游四字符一 token、图片 1200 token 的规则估算会话。</summary>
public static class TokenEstimation
{
    /// <summary>优先使用非零总量，否则合计输入、输出和缓存；缓存细分与推理子集不重复计数。</summary>
    /// <param name="usage">供应商返回的用量。</param>
    /// <returns>上下文用量。</returns>
    public static long CalculateContextTokens(Usage usage) => usage.TotalTokens is { } total && total != 0
        ? total : (long)usage.InputTokens + usage.OutputTokens + (usage.CacheReadTokens ?? 0) + (usage.CacheWriteTokens ?? 0);

    /// <summary>按 UTF-16 字符数估算文本，每四个字符向上取整。</summary>
    /// <param name="text">待估算文本。</param>
    /// <returns>token 估值。</returns>
    public static long EstimateTextTokens(string text) => (text.Length + 3L) / 4;

    /// <summary>合计文字字符数和图片占位成本后统一取整。</summary>
    /// <param name="content">用户消息或工具结果内容。</param>
    /// <returns>token 估值。</returns>
    public static long EstimateTextAndImageContentTokens(IReadOnlyList<ContentBlock> content) =>
        (content.Sum(block => block is TextContent text ? (long)text.Text.Length : 4_800) + 3) / 4;

    /// <summary>按上游工具声明字段估算 JSON 数组，忽略 .NET 属性名和格式化空白。</summary>
    /// <param name="tools">新增或现有工具声明。</param>
    /// <returns>token 估值，空集合返回零。</returns>
    public static long EstimateToolsTokens(IReadOnlyList<Tool>? tools) => tools is not { Count: > 0 } ? 0 :
        (CountJsonCharacters(SystemMessageJson.ToElement(new("") { ToolsAdded = tools }).GetProperty("toolsAdded")) + 3) / 4;

    /// <summary>估算移除工具的名称引用数组。</summary>
    /// <param name="tools">移除的工具引用。</param>
    /// <returns>token 估值，空集合返回零。</returns>
    public static long EstimateToolReferencesTokens(IReadOnlyList<ToolReference>? tools) => tools is not { Count: > 0 } ? 0 :
        (CountJsonCharacters(SystemMessageJson.ToElement(new("") { ToolsRemoved = tools }).GetProperty("toolsRemoved")) + 3) / 4;

    /// <summary>按消息角色估算正文、思考、工具调用以及系统声明增量。</summary>
    /// <param name="message">单条原始会话消息。</param>
    /// <returns>token 估值，不计签名和图片 base64 长度。</returns>
    public static long EstimateMessageTokens(ChatMessage message) => message switch
    {
        SystemMessage system => EstimateTextTokens(Transcript.GetSystemMessageText(system)) +
            EstimateToolsTokens(system.ToolsAdded) + EstimateToolReferencesTokens(system.ToolsRemoved),
        UserMessage user => EstimateTextAndImageContentTokens(user.Content),
        ToolResultMessage result => EstimateTextAndImageContentTokens(result.Content),
        AssistantMessage assistant => (assistant.Content.Sum(block => block switch
        {
            TextContent text => (long)text.Text.Length,
            ThinkingContent thinking => thinking.Thinking.Length,
            ToolCallContent tool => tool.Name.Length + CountArgumentsCharacters(tool.Arguments),
            _ => 0L
        }) + 3) / 4,
        _ => 0
    };

    /// <summary>估算消息式上下文；旧顶层提示和工具需先通过 Transcript.NormalizeContext 转换。</summary>
    /// <param name="context">待估算的消息式上下文。</param>
    /// <returns>总量与估算来源。</returns>
    public static ContextUsageEstimate EstimateContextTokens(LlmContext context) => EstimateContextTokens(context.Messages);

    /// <summary>【AI】【上下文估算】忽略压缩重排后失效、错误或取消的用量，再累计尾部消息。</summary>
    /// <param name="messages">按重放顺序排列的消息；旧消息缺失时间戳时按 Unix 起点处理。</param>
    /// <returns>最近有效用量加尾部估算，没有有效用量时估算全部消息。</returns>
    public static ContextUsageEstimate EstimateContextTokens(IReadOnlyList<ChatMessage> messages)
    {
        // 1. 【AI】【上下文估算】用量的时间不得早于此前任何一条消息
        var latestTimestamp = DateTimeOffset.MinValue;
        int? lastIndex = null;
        long usageTokens = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var timestamp = message switch
            {
                SystemMessage system => system.Timestamp,
                UserMessage user => user.Timestamp ?? DateTimeOffset.UnixEpoch,
                AssistantMessage assistant => assistant.Timestamp ?? DateTimeOffset.UnixEpoch,
                ToolResultMessage result => result.Timestamp ?? DateTimeOffset.UnixEpoch,
                _ => DateTimeOffset.UnixEpoch
            };
            if (message is AssistantMessage { Usage: { } usage } response && timestamp >= latestTimestamp &&
                response.StopReason is not StopReason.Error and not StopReason.Aborted && CalculateContextTokens(usage) > 0)
            {
                usageTokens = CalculateContextTokens(usage);
                lastIndex = index;
            }
            if (timestamp > latestTimestamp) latestTimestamp = timestamp;
        }

        // 2. 【AI】【上下文估算】保留有效前缀的服务端计数，仅估算其后的消息
        long trailingTokens = 0;
        for (var index = (lastIndex ?? -1) + 1; index < messages.Count; index++)
            trailingTokens += EstimateMessageTokens(messages[index]);
        return new(usageTokens + trailingTokens, usageTokens, trailingTokens, lastIndex);
    }

    /// <summary>去除工具参数的 JSON 格式空白，损坏参数使用上游不可序列化占位文本的长度。</summary>
    /// <param name="arguments">工具调用的 JSON 参数。</param>
    /// <returns>紧凑 JSON 字符数。</returns>
    private static long CountArgumentsCharacters(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return CountJsonCharacters(document.RootElement);
        }
        catch (JsonException) { return "[unserializable]".Length; }
    }

    /// <summary>计算 JSON.stringify 风格的字符数，避免 .NET 的中文和补充平面转义放大估值。</summary>
    /// <param name="value">JSON 值。</param>
    /// <returns>无缩进 JSON 字符数。</returns>
    private static long CountJsonCharacters(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => 2 + value.EnumerateObject().Sum(property =>
            CountQuotedCharacters(property.Name) + 1 + CountJsonCharacters(property.Value)) + Math.Max(0, value.EnumerateObject().Count() - 1),
        JsonValueKind.Array => 2 + value.EnumerateArray().Sum(CountJsonCharacters) + Math.Max(0, value.GetArrayLength() - 1),
        JsonValueKind.String => CountQuotedCharacters(value.GetString()!),
        JsonValueKind.Number => CountNumberCharacters(value.GetDouble()),
        JsonValueKind.True => 4,
        JsonValueKind.False => 5,
        JsonValueKind.Null => 4,
        _ => "undefined".Length
    };

    /// <summary>计算 JSON 字符串的引号与必要转义长度，普通 Unicode 字符保持 UTF-16 计数。</summary>
    /// <param name="text">原始字符串。</param>
    /// <returns>含双引号的 JSON 字符数。</returns>
    private static long CountQuotedCharacters(string text) => 2 + text.Sum(character => character switch
    {
        '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t' => 2L,
        < ' ' => 6L,
        _ => 1L
    });

    /// <summary>按照 JavaScript 的十进制与指数分界计算数字长度，避免原始 JSON 格式影响估算。</summary>
    /// <param name="number">转换为 JavaScript 数值精度的数字。</param>
    /// <returns>数字的紧凑字符数。</returns>
    private static long CountNumberCharacters(double number)
    {
        if (!double.IsFinite(number)) return 4;
        if (number == 0) return 1;
        var text = Math.Abs(number).ToString("G", CultureInfo.InvariantCulture);
        var sign = number < 0 ? 1 : 0;
        var exponentIndex = text.IndexOf('E');
        if (exponentIndex < 0) return sign + text.Length;
        var exponent = int.Parse(text.AsSpan(exponentIndex + 1), CultureInfo.InvariantCulture);
        var digits = text[..exponentIndex].Replace(".", "").Length;
        if (exponent is >= 0 and < 21) return sign + (digits <= exponent + 1 ? exponent + 1 : digits + 1);
        if (exponent is >= -6 and < 0) return sign + 2 - exponent - 1 + digits;
        return sign + exponentIndex + 2 + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).Length;
    }
}
