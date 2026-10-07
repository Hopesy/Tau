// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Serialization;

/// <summary>【AI】【系统消息序列化】为扩展与 RPC 输出上游字段格式的系统消息。</summary>
public static class SystemMessageJson
{
    /// <summary>读取扩展返回的系统消息，工具 schema 克隆到独立内存。</summary>
    /// <param name="value">使用上游字段名称的消息 JSON。</param>
    /// <returns>系统消息；损坏结构抛出 JSON 或字段类型异常。</returns>
    public static SystemMessage Read(JsonElement value)
    {
        var content = value.GetProperty("content");
        var text = content.ValueKind == JsonValueKind.String ? content.GetString()! :
            string.Join("\n", content.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "text").Select(block => block.GetProperty("text").GetString()));
        return new SystemMessage(text)
        {
            Timestamp = value.TryGetProperty("timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.GetInt64()) : DateTimeOffset.UnixEpoch,
            Sections = value.TryGetProperty("sections", out var sections) && sections.ValueKind != JsonValueKind.Null
                ? sections.EnumerateObject().ToDictionary(section => section.Name, section => section.Value.GetString(), StringComparer.Ordinal) : null,
            ToolsAdded = value.TryGetProperty("toolsAdded", out var added) && added.ValueKind != JsonValueKind.Null
                ? added.EnumerateArray().Select(tool => new Tool(tool.GetProperty("name").GetString()!, tool.GetProperty("description").GetString()!, tool.GetProperty("parameters").Clone())
                {
                    ConstrainedSampling = tool.TryGetProperty("constrainedSampling", out var sampling) && sampling.ValueKind != JsonValueKind.Null
                        ? sampling.Deserialize(TauAiJsonContext.Default.ConstrainedSamplingConfig) : null
                }).ToArray() : null,
            ToolsRemoved = value.TryGetProperty("toolsRemoved", out var removed) && removed.ValueKind != JsonValueKind.Null
                ? removed.EnumerateArray().Select(tool => new ToolReference(tool.GetProperty("name").GetString()!)).ToArray() : null
        };
    }

    /// <summary>写入完整系统消息对象。</summary>
    /// <param name="writer">调用方拥有的 JSON 写入器。</param>
    /// <param name="message">系统消息。</param>
    public static void Write(Utf8JsonWriter writer, SystemMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "system");
        writer.WriteString("content", message.Content);
        writer.WriteNumber("timestamp", message.Timestamp.ToUnixTimeMilliseconds());
        if (message.Sections is { } sections)
        {
            writer.WriteStartObject("sections");
            foreach (var (name, text) in sections) writer.WriteString(name, text);
            writer.WriteEndObject();
        }
        if (message.ToolsAdded is { } added)
        {
            writer.WriteStartArray("toolsAdded");
            foreach (var tool in added)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("parameters");
                tool.ParameterSchema.WriteTo(writer);
                if (tool.ConstrainedSampling is { } sampling)
                {
                    writer.WritePropertyName("constrainedSampling");
                    JsonSerializer.Serialize(writer, sampling, TauAiJsonContext.Default.ConstrainedSamplingConfig);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (message.ToolsRemoved is { } removed)
        {
            writer.WriteStartArray("toolsRemoved");
            foreach (var tool in removed)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary>创建独立持有内存的系统消息 JSON。</summary>
    /// <param name="message">待转换的消息。</param>
    /// <returns>可以安全跨异步调用使用的 JSON 节点。</returns>
    public static JsonElement ToElement(SystemMessage message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, message);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
