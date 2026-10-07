// 作者：xxx
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【工具协议】序列化程序调用者可见的完整工具结果。</summary>
    /// <param name="result">最终结果或中间更新。</param>
    /// <returns>独立于临时缓冲区的 JSON 对象。</returns>
    internal static JsonElement SerializeToolResult(ToolResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteToolResult(writer, result);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>【CodingAgent】【工具用量】读取上游短字段和旧 Tau 字段名称。</summary>
    /// <param name="root">包含可选 usage 的结果。</param>
    /// <returns>工具用量，缺失时为空。</returns>
    private static Usage? ReadToolUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var input = ReadUsageCount(value, "input", "inputTokens");
        var output = ReadUsageCount(value, "output", "outputTokens");
        var read = ReadUsageCount(value, "cacheRead", "cacheReadTokens");
        var write = ReadUsageCount(value, "cacheWrite", "cacheWriteTokens");
        UsageCost? cost = null;
        if (value.TryGetProperty("cost", out var price) && price.ValueKind == JsonValueKind.Object)
            cost = new(ReadUsagePrice(price, "input"), ReadUsagePrice(price, "output"), ReadUsagePrice(price, "cacheRead"), ReadUsagePrice(price, "cacheWrite"));
        return new Usage(input, output, read, write, Cost: cost)
        { TotalTokens = value.TryGetProperty("totalTokens", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var count) ? count : input + output + read + write };
    }

    /// <summary>【CodingAgent】【工具用量】读取可选整数计数。</summary>
    /// <param name="value">用量对象。</param>
    /// <param name="name">标准名称。</param>
    /// <param name="alias">旧名称。</param>
    /// <returns>计数，缺失时为零。</returns>
    private static int ReadUsageCount(JsonElement value, string name, string alias) =>
        (value.TryGetProperty(name, out var count) || value.TryGetProperty(alias, out count)) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var result) ? result : 0;

    /// <summary>【CodingAgent】【工具用量】读取可选费用维度。</summary>
    /// <param name="value">费用对象。</param>
    /// <param name="name">维度名称。</param>
    /// <returns>费用，缺失时为零。</returns>
    private static decimal ReadUsagePrice(JsonElement value, string name) =>
        value.TryGetProperty(name, out var price) && price.ValueKind == JsonValueKind.Number && price.TryGetDecimal(out var result) ? result : 0;

    /// <summary>【CodingAgent】【工具用量】按上游结构写入用量，不改变会话 DTO 格式。</summary>
    /// <param name="writer">JSON 写入器。</param>
    /// <param name="usage">工具用量。</param>
    private static void WriteToolUsage(Utf8JsonWriter writer, Usage usage)
    {
        writer.WriteStartObject();
        writer.WriteNumber("input", usage.InputTokens); writer.WriteNumber("output", usage.OutputTokens);
        writer.WriteNumber("cacheRead", usage.CacheReadTokens.GetValueOrDefault()); writer.WriteNumber("cacheWrite", usage.CacheWriteTokens.GetValueOrDefault());
        writer.WriteNumber("totalTokens", usage.TotalTokens ?? usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens.GetValueOrDefault() + usage.CacheWriteTokens.GetValueOrDefault());
        if (usage.Cost is { } cost)
        {
            writer.WriteStartObject("cost");
            writer.WriteNumber("input", cost.Input); writer.WriteNumber("output", cost.Output);
            writer.WriteNumber("cacheRead", cost.CacheRead); writer.WriteNumber("cacheWrite", cost.CacheWrite);
            writer.WriteNumber("total", cost.Total); writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}
