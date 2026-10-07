// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【会话兼容】读取 Tau 和 pi 两种消息字段格式，平面格式继续使用 Tau 字段。</summary>
internal class CodingAgentSessionMessageConverter : JsonConverter<CodingAgentSessionMessage>
{
    private static readonly JsonSerializerOptions ArgumentsOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    /// <summary>【CodingAgent】【消息读取】规范化时间、内容、工具参数、停止原因及用量后读取消息。</summary>
    /// <param name="reader">JSON 读取器。</param>
    /// <param name="typeToConvert">消息 DTO 类型。</param>
    /// <param name="options">序列化选项。</param>
    /// <returns>保留协议元数据的 DTO。</returns>
    public override CodingAgentSessionMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var root = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("Session message must be an object.");
        // 1. 【CodingAgent】【消息读取】pi 使用毫秒时间及字符串用户内容，Tau 内部统一为时间对象和内容块
        if (root["timestamp"] is JsonValue time && time.TryGetValue<long>(out var epoch))
        {
            if (epoch < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || epoch > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
                throw new JsonException("Session message timestamp is outside the supported range.");
            root["timestamp"] = DateTimeOffset.FromUnixTimeMilliseconds(epoch).ToString("O");
        }
        if (root["content"] is JsonValue text && text.TryGetValue<string>(out var content))
            root["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = content });
        root["content"] ??= new JsonArray();
        if (root["toolsAdded"] is JsonArray declarations)
            foreach (var tool in declarations.OfType<JsonObject>()) CopyAlias(tool, "parameters", "parameterSchema");
        if (root["content"] is JsonArray blocks)
            foreach (var block in blocks.OfType<JsonObject>())
                if (block["type"]?.GetValue<string>() == "toolCall" && block["arguments"] is JsonObject or JsonArray)
                    block["arguments"] = block["arguments"]!.ToJsonString(ArgumentsOptions);
        // 2. 【CodingAgent】【用量兼容】接受上游短字段名，旧文件的明确字段优先
        if (root["usage"] is JsonObject usage)
        {
            CopyAlias(usage, "input", "inputTokens");
            CopyAlias(usage, "output", "outputTokens");
            CopyAlias(usage, "cacheRead", "cacheReadTokens");
            CopyAlias(usage, "cacheWrite", "cacheWriteTokens");
        }
        if (root["stopReason"] is JsonValue reason && reason.TryGetValue<string>(out var stop))
        {
            StopReason? parsed = stop switch
            {
                "stop" => StopReason.EndTurn, "length" => StopReason.MaxTokens, "toolUse" => StopReason.ToolUse,
                "contentFilter" => StopReason.ContentFilter, "error" => StopReason.Error,
                "aborted" => StopReason.Aborted, "deferred" => StopReason.Deferred,
                _ => Enum.TryParse<StopReason>(stop, true, out var value) ? value : null
            };
            root["stopReason"] = parsed is null ? null : JsonValue.Create((int)parsed.Value);
            if (parsed is null) root["rawStopReason"] ??= stop;
        }
        return root.Deserialize(CodingAgentRawSessionJsonContext.Default.CodingAgentSessionMessage);
    }

    /// <summary>【CodingAgent】【平面保存】沿用 Tau 平面字段格式，避免改变旧兼容文件的读取约定。</summary>
    /// <param name="writer">JSON 写入器。</param>
    /// <param name="value">消息 DTO。</param>
    /// <param name="options">序列化选项。</param>
    public override void Write(Utf8JsonWriter writer, CodingAgentSessionMessage value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, CodingAgentRawSessionJsonContext.Default.CodingAgentSessionMessage);

    /// <summary>【CodingAgent】【字段兼容】仅在标准字段缺失时复制别名。</summary>
    /// <param name="value">JSON 对象。</param>
    /// <param name="alias">别名。</param>
    /// <param name="name">内部字段名。</param>
    private static void CopyAlias(JsonObject value, string alias, string name)
    {
        if (value[name] is null && value[alias] is { } source) value[name] = source.DeepClone();
    }
}

/// <summary>【CodingAgent】【JSONL消息】写入 pi 的消息格式，同时通过基类兼容旧 Tau 文件。</summary>
internal sealed class CodingAgentPiSessionMessageConverter : CodingAgentSessionMessageConverter
{
    /// <summary>【CodingAgent】【JSONL保存】输出标准毫秒时间、对象工具参数和用量字段。</summary>
    /// <param name="writer">JSON 写入器。</param>
    /// <param name="value">消息 DTO。</param>
    /// <param name="options">序列化选项。</param>
    public override void Write(Utf8JsonWriter writer, CodingAgentSessionMessage value, JsonSerializerOptions options)
    {
        var root = JsonSerializer.SerializeToNode(value, CodingAgentRawSessionJsonContext.Default.CodingAgentSessionMessage)!.AsObject();
        if (value.Timestamp is { } timestamp) root["timestamp"] = timestamp.ToUnixTimeMilliseconds();
        if (value.Role == "system") root["content"] = string.Join("\n", value.Content.Select(block => block.Text ?? ""));
        if (root["toolsAdded"] is JsonArray declarations)
            foreach (var tool in declarations.OfType<JsonObject>()) Rename(tool, "parameterSchema", "parameters");
        if (root["content"] is JsonArray blocks)
            foreach (var block in blocks.OfType<JsonObject>())
                if (block["type"]?.GetValue<string>() == "toolCall" && block["arguments"] is JsonValue arguments && arguments.TryGetValue<string>(out var json))
                {
                    try { block["arguments"] = JsonNode.Parse(json); }
                    catch (JsonException) { }
                }
        if (root["usage"] is JsonObject usage)
        {
            Rename(usage, "inputTokens", "input"); Rename(usage, "outputTokens", "output");
            Rename(usage, "cacheReadTokens", "cacheRead"); Rename(usage, "cacheWriteTokens", "cacheWrite");
            usage["cacheRead"] ??= 0; usage["cacheWrite"] ??= 0;
            usage["totalTokens"] ??= (value.Usage?.InputTokens ?? 0) + (value.Usage?.OutputTokens ?? 0) +
                (value.Usage?.CacheReadTokens ?? 0) + (value.Usage?.CacheWriteTokens ?? 0);
        }
        if (value.StopReason is { } stop) root["stopReason"] = stop switch
        {
            StopReason.EndTurn => "stop", StopReason.MaxTokens => "length", StopReason.ToolUse => "toolUse",
            StopReason.ContentFilter => "contentFilter", StopReason.Error => "error",
            StopReason.Aborted => "aborted", StopReason.Deferred => "deferred", _ => stop.ToString()
        };
        root.WriteTo(writer);
    }

    /// <summary>【CodingAgent】【字段写入】移动字段到上游标准名称。</summary>
    /// <param name="value">JSON 对象。</param>
    /// <param name="oldName">内部名称。</param>
    /// <param name="newName">标准名称。</param>
    private static void Rename(JsonObject value, string oldName, string newName)
    {
        if (value.Remove(oldName, out var node)) value[newName] = node;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CodingAgentSessionMessage))]
internal sealed partial class CodingAgentRawSessionJsonContext : JsonSerializerContext;
