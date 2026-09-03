using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Collections;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.AgentCore.Harness.Session;

internal sealed record JsonlSessionHeader(
    string Type,
    int Version,
    string Id,
    string Timestamp,
    string Cwd,
    string? ParentSession = null)
{
    /// <summary>v4 header 的父 session id。</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>v4 header 无法解析的旧父 session 路径。</summary>
    public string? LegacyParentSessionPath { get; init; }

    /// <summary>v4 header 的应用元数据。</summary>
    public JsonElement? Metadata { get; init; }
}

internal sealed record JsonlV4HeaderDto(
    string Kind,
    int Version,
    string Id,
    long CreatedAt,
    string Cwd,
    string? ParentSessionId = null,
    string? LegacyParentSessionPath = null,
    JsonElement? Metadata = null);

internal sealed record JsonlSessionEntryDto
{
    public string? Type { get; init; }
    public string? Id { get; init; }
    public long? Seq { get; init; }
    public string? Lane { get; init; }
    public string? ParentId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public SessionMessageDto? Message { get; init; }
    public bool? Terminate { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? Provider { get; init; }
    public string? ModelId { get; init; }
    public IReadOnlyList<string>? ActiveToolNames { get; init; }
    public string? FirstKeptEntryId { get; init; }
    public int? TokensBefore { get; init; }
    public JsonElement? RetainedTail { get; init; }
    public SessionUsageDto? Usage { get; init; }
    public string? CustomType { get; init; }
    public JsonElement? Data { get; init; }
    public JsonElement? Content { get; init; }
    public bool? Display { get; init; }
    public string? TargetId { get; init; }
    public string? Label { get; init; }
    public string? Name { get; init; }
    public string? FromId { get; init; }
    public string? Summary { get; init; }
    public JsonElement? Details { get; init; }
    public bool? FromHook { get; init; }
}

internal sealed record JsonlV4OperationIntentDto
{
    public string? Kind { get; init; }
    public JsonElement? OriginalPrompt { get; init; }
    public JsonElement? InitialMessages { get; init; }
    public string? SystemPromptOverride { get; init; }
    public JsonElement? ResumeData { get; init; }
    public string? CustomInstructions { get; init; }
    public string? ResultEntryId { get; init; }
    public string? TargetId { get; init; }
    public bool? Summarize { get; init; }
    public string? Label { get; init; }
    public string? SummaryEntryId { get; init; }
}

internal sealed record SessionMessageDto
{
    public string? Role { get; init; }
    /// <summary>消息内容；标准消息使用内容块数组，custom 消息也允许使用纯文本字符串。</summary>
    public JsonElement? Content { get; init; }
    public SessionUsageDto? Usage { get; init; }
    public string? Api { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ResponseModel { get; init; }
    public string? ResponseId { get; init; }
    public string? StopReason { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawStopReason { get; init; }
    public bool? EndTurn { get; init; }
    public JsonElement? Diagnostics { get; init; }
    public SessionDeferredHandleDto? Deferred { get; init; }
    public JsonElement? Details { get; init; }
    public IReadOnlyList<string>? AddedToolNames { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool? IsError { get; init; }
    public string? Command { get; init; }
    public string? Output { get; init; }
    public int? ExitCode { get; init; }
    public bool? Cancelled { get; init; }
    public bool? Truncated { get; init; }
    public string? FullOutputPath { get; init; }
    public bool? ExcludeFromContext { get; init; }
    public string? CustomType { get; init; }
    public bool? Display { get; init; }
    public string? Summary { get; init; }
    public string? FromId { get; init; }
    public int? TokensBefore { get; init; }
}

internal sealed record SessionDeferredHandleDto
{
    public string? Provider { get; init; }
    public string? ModelId { get; init; }
    public string? Api { get; init; }
    public string? Id { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int? PollAfterMs { get; init; }
    public JsonElement? Data { get; init; }
}

internal sealed record SessionUsageDto
{
    /// <summary>v4 usage input token 数量。</summary>
    public int? Input { get; init; }
    /// <summary>v4 usage output token 数量。</summary>
    public int? Output { get; init; }
    /// <summary>v4 usage cache read token 数量。</summary>
    public int? CacheRead { get; init; }
    /// <summary>v4 usage cache write token 数量。</summary>
    public int? CacheWrite { get; init; }
    /// <summary>Anthropic 1 小时 cache write token 数量。</summary>
    public int? CacheWrite1h { get; init; }
    /// <summary>推理 token 数量。</summary>
    public int? Reasoning { get; init; }
    /// <summary>总 token 数量。</summary>
    public int? TotalTokens { get; init; }

    // 兼容 Tau 旧版 JSONL 字段，读取时保留，写入时不再生成
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int InputTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OutputTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? CacheReadTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? CacheWriteTokens { get; init; }
    public string? ServiceTier { get; init; }
    public SessionUsageCostDto? Cost { get; init; }
}

internal sealed record SessionUsageCostDto
{
    public decimal Input { get; init; }
    public decimal Output { get; init; }
    public decimal CacheRead { get; init; }
    public decimal CacheWrite { get; init; }
    public decimal Total { get; init; }
}

internal sealed record SessionContentDto
{
    public string? Type { get; init; }
    public string? Text { get; init; }
    public string? TextSignature { get; init; }
    public string? Thinking { get; init; }
    public string? ThinkingSignature { get; init; }
    public bool Redacted { get; init; }
    public string? Data { get; init; }
    public string? MimeType { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    /// <summary>toolCall 参数；v4 使用 JSON 对象，读取旧 v3 时也允许字符串。</summary>
    public JsonElement? Arguments { get; init; }
    public string? ThoughtSignature { get; init; }
}

internal static class JsonlSessionSerialization
{
    public const int CurrentVersion = 3;

    public static string SerializeHeader(JsonlSessionHeader header) =>
        JsonSerializer.Serialize(header, AgentCoreSessionJsonContext.Default.JsonlSessionHeader);

    /// <summary>
    /// 序列化 pi v4 JSONL session header。
    /// </summary>
    /// <param name="id">session id。</param>
    /// <param name="createdAt">创建时间（Unix 毫秒）。</param>
    /// <param name="cwd">session 工作目录。</param>
    /// <param name="parentSessionId">父 session id。</param>
    /// <param name="legacyParentSessionPath">无法解析的旧父 session 路径。</param>
    /// <param name="metadata">应用自定义元数据。</param>
    /// <returns>带换行符的 v4 header JSON 行。</returns>
    public static string SerializeV4Header(
        string id,
        long createdAt,
        string cwd,
        string? parentSessionId = null,
        string? legacyParentSessionPath = null,
        JsonElement? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Session id is required.", nameof(id));
        if (createdAt < 0) throw new ArgumentOutOfRangeException(nameof(createdAt));
        if (string.IsNullOrWhiteSpace(cwd)) throw new ArgumentException("Session cwd is required.", nameof(cwd));
        if (parentSessionId is not null && legacyParentSessionPath is not null)
            throw new ArgumentException("v4 header cannot contain both parent session id and legacy path.");
        if (metadata is { } metadataValue && metadataValue.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("v4 header metadata must be a JSON object.", nameof(metadata));

        return SerializeJsonLine(writer =>
        {
            writer.WriteString("kind", "header");
            writer.WriteNumber("version", 4);
            writer.WriteString("id", id);
            writer.WriteNumber("createdAt", createdAt);
            writer.WriteString("cwd", cwd);
            if (parentSessionId is not null)
                writer.WriteString("parentSessionId", parentSessionId);
            if (legacyParentSessionPath is not null)
                writer.WriteString("legacyParentSessionPath", legacyParentSessionPath);
            if (metadata is { } metadataValue)
            {
                writer.WritePropertyName("metadata");
                metadataValue.WriteTo(writer);
            }
        });
    }

    public static string SerializeEntry(SessionTreeEntry entry) =>
        AddEntryMutationKind(JsonSerializer.Serialize(ToDto(entry), AgentCoreSessionJsonContext.Default.JsonlSessionEntryDto));

    /// <summary>按 pi v4 wire schema 序列化 entry mutation。</summary>
    /// <param name="entry">待序列化 entry。</param>
    /// <returns>符合 v4 EntryBase 的 JSONL 行。</returns>
    public static string SerializeV4Entry(SessionTreeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Sequence <= 0) throw new ArgumentOutOfRangeException(nameof(entry), "v4 entry sequence must be positive.");
        if (entry.Timestamp < DateTimeOffset.UnixEpoch) throw new ArgumentOutOfRangeException(nameof(entry), "v4 entry timestamp must be non-negative.");
        var dto = ToDto(entry);
        var isCompaction = entry is CompactionSessionEntry;
        var dtoNode = JsonNode.Parse(JsonSerializer.Serialize(dto, AgentCoreSessionJsonContext.Default.JsonlSessionEntryDto)) ?? throw new InvalidOperationException("Unable to serialize session entry.");
        NormalizeNestedMessageTimestamps(dtoNode, toUnixMilliseconds: true);
        using var document = JsonDocument.Parse(dtoNode.ToJsonString());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "entry");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("timestamp"))
                {
                    writer.WriteNumber("timestamp", entry.Timestamp.ToUnixTimeMilliseconds());
                    continue;
                }
                property.WriteTo(writer);
            }
            if (isCompaction && !document.RootElement.TryGetProperty("retainedTail", out _))
            {
                // v4 compaction 必须提供 retainedTail；旧 Tau entry 没有尾部时使用空数组
                writer.WriteStartArray("retainedTail");
                writer.WriteEndArray();
            }
            if (!document.RootElement.TryGetProperty("parentId", out _)) writer.WriteNull("parentId");
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>为 v4 entry mutation 添加 kind 标识并输出单行 JSON。</summary>
    /// <param name="serializedEntry">已序列化的 entry JSON。</param>
    /// <returns>带 kind 字段的 v4 entry JSON。</returns>
    private static string AddEntryMutationKind(string serializedEntry)
    {
        using var document = JsonDocument.Parse(serializedEntry);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "entry");
            // pi v4 EntryBase 要求 parentId 即使为空也必须出现在 JSON 中
            if (!document.RootElement.TryGetProperty("parentId", out _))
                writer.WriteNull("parentId");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("timestamp") && property.Value.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(property.Value.GetString(), out var timestamp))
                {
                    writer.WriteNumber("timestamp", timestamp.ToUnixTimeMilliseconds());
                    continue;
                }

                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>在 session entry 内递归转换 AgentMessage 的 timestamp 表示。</summary>
    /// <param name="node">待处理的 JSON 节点。</param>
    /// <param name="toUnixMilliseconds">为 true 时转换为 Unix 毫秒，否则转换为 ISO 文本。</param>
    private static void NormalizeNestedMessageTimestamps(JsonNode node, bool toUnixMilliseconds)
    {
        if (node is JsonObject obj)
        {
            if (obj["role"] is JsonValue && obj["timestamp"] is JsonValue timestamp)
            {
                if (toUnixMilliseconds && timestamp.TryGetValue<string>(out var iso) && DateTimeOffset.TryParse(iso, out var parsed))
                    obj["timestamp"] = parsed.ToUnixTimeMilliseconds();
                else if (!toUnixMilliseconds && timestamp.TryGetValue<long>(out var unix) && unix >= 0)
                    obj["timestamp"] = DateTimeOffset.FromUnixTimeMilliseconds(unix).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (obj["expiresAt"] is JsonValue expiresAt)
            {
                if (toUnixMilliseconds && expiresAt.TryGetValue<string>(out var expiresIso) && DateTimeOffset.TryParse(expiresIso, out var expiresParsed))
                    obj["expiresAt"] = expiresParsed.ToUnixTimeMilliseconds();
                else if (!toUnixMilliseconds && expiresAt.TryGetValue<long>(out var expiresUnix) && expiresUnix >= 0)
                    obj["expiresAt"] = DateTimeOffset.FromUnixTimeMilliseconds(expiresUnix).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            }
            foreach (var property in obj.ToArray())
                if (property.Value is not null) NormalizeNestedMessageTimestamps(property.Value, toUnixMilliseconds);
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                if (item is not null) NormalizeNestedMessageTimestamps(item, toUnixMilliseconds);
        }
    }

    /// <summary>序列化 v4 lane 指针 mutation。</summary>
    /// <param name="sequence">共享序号。</param>
    /// <param name="lane">lane 名称。</param>
    /// <param name="leafId">目标叶 entry id。</param>
    /// <returns>单行 JSON。</returns>
    public static string SerializeLaneMutation(long sequence, string lane, string? leafId)
    {
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence), "v4 lane sequence must be positive.");
        if (string.IsNullOrWhiteSpace(lane)) throw new ArgumentException("Lane is required.", nameof(lane));
        return SerializeJsonLine(writer =>
        {
            writer.WriteString("kind", "lane");
            writer.WriteNumber("seq", sequence);
            writer.WriteString("lane", lane);
            if (leafId is null)
                writer.WriteNull("leafId");
            else
                writer.WriteString("leafId", leafId);
        });
    }

    /// <summary>序列化 v4 全局 fact mutation。</summary>
    /// <param name="sequence">共享序号。</param>
    /// <param name="fact">fact 类型，name 或 label。</param>
    /// <param name="name">session 名称。</param>
    /// <param name="targetId">label 目标 entry id。</param>
    /// <param name="label">label 文本。</param>
    /// <returns>单行 JSON。</returns>
    public static string SerializeFactMutation(long sequence, string fact, string? name = null, string? targetId = null, string? label = null)
    {
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence), "v4 fact sequence must be positive.");
        if (fact is not ("name" or "label")) throw new ArgumentException("Fact must be name or label.", nameof(fact));
        if (fact == "label" && string.IsNullOrWhiteSpace(targetId)) throw new ArgumentException("Label fact requires targetId.", nameof(targetId));
        return SerializeJsonLine(writer =>
        {
            writer.WriteString("kind", "fact");
            writer.WriteNumber("seq", sequence);
            writer.WriteString("fact", fact);
            if (fact.Equals("name", StringComparison.Ordinal))
            {
                if (name is null)
                    writer.WriteNull("name");
                else
                    writer.WriteString("name", name);
            }
            else
            {
                if (targetId is null)
                    writer.WriteNull("targetId");
                else
                    writer.WriteString("targetId", targetId);
                if (label is null)
                    writer.WriteNull("label");
                else
                    writer.WriteString("label", label);
            }
        });
    }

    /// <summary>使用 Utf8JsonWriter 生成一行无反射 JSON，兼容 NativeAOT。</summary>
    /// <param name="writeProperties">写入对象属性的委托。</param>
    /// <returns>带换行符的 JSON 行。</returns>
    private static string SerializeJsonLine(Action<Utf8JsonWriter> writeProperties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static JsonlSessionHeader ParseHeader(string line, string filePath)
    {
        // 1. 先识别 pi 0.84.4 使用的 v4 header
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("kind", out var kind) &&
                kind.GetString() == "header")
            {
                var version = root.TryGetProperty("version", out var versionValue) && versionValue.TryGetInt32(out var parsedVersion)
                    ? parsedVersion
                    : 0;
                if (version != 4 ||
                    !root.TryGetProperty("id", out var idValue) ||
                    idValue.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(idValue.GetString()) ||
                    !root.TryGetProperty("createdAt", out var createdAtValue) ||
                    createdAtValue.ValueKind != JsonValueKind.Number ||
                    !createdAtValue.TryGetInt64(out var createdAt) ||
                    createdAt < 0 ||
                    !root.TryGetProperty("cwd", out var cwdValue) ||
                    cwdValue.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(cwdValue.GetString()))
                {
                    throw InvalidSession(filePath, "first line is not a valid v4 session header");
                }

                var parentSessionId = ReadOptionalHeaderString(root, "parentSessionId", filePath);
                var legacyParent = ReadOptionalHeaderString(root, "legacyParentSessionPath", filePath);
                if (parentSessionId is not null && legacyParent is not null)
                    throw InvalidSession(filePath, "v4 header contains both parentSessionId and legacyParentSessionPath");

                return new JsonlSessionHeader(
                    "session",
                    4,
                    idValue.GetString()!,
                    DateTimeOffset.FromUnixTimeMilliseconds(createdAt).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    cwdValue.GetString()!,
                    parentSessionId ?? legacyParent)
                {
                    ParentSessionId = parentSessionId,
                    LegacyParentSessionPath = legacyParent,
                    Metadata = ReadOptionalHeaderObject(root, "metadata", filePath)
                };
            }
        }
        catch (JsonException)
        {
            // 2. 由下面的 v3 解析路径统一生成标准错误
        }

        // 3. 保留 Tau v3 header 的兼容读取
        JsonlSessionHeader? header;
        try
        {
            header = JsonSerializer.Deserialize(line, AgentCoreSessionJsonContext.Default.JsonlSessionHeader);
        }
        catch (JsonException ex)
        {
            throw InvalidSession(filePath, "first line is not a valid session header", ex);
        }

        if (header is null ||
            header.Type != "session" ||
            (header.Version != CurrentVersion && header.Version != 4) ||
            string.IsNullOrWhiteSpace(header.Id) ||
            string.IsNullOrWhiteSpace(header.Timestamp) ||
            string.IsNullOrWhiteSpace(header.Cwd))
        {
            throw InvalidSession(filePath, "first line is not a valid session header");
        }

        return header;
    }

    /// <summary>读取 v4 header 中可选的字符串字段，并拒绝 null 或错类型。</summary>
    /// <param name="root">header JSON 对象。</param>
    /// <param name="name">字段名称。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <returns>字段值；字段不存在时返回 null。</returns>
    private static string? ReadOptionalHeaderString(JsonElement root, string name, string filePath)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw InvalidSession(filePath, $"v4 header has invalid {name}");
        return value.GetString();
    }

    /// <summary>读取 v4 header 中可选的 metadata 对象。</summary>
    /// <param name="root">header JSON 对象。</param>
    /// <param name="name">字段名称。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <returns>metadata JSON；字段不存在时返回 null。</returns>
    private static JsonElement? ReadOptionalHeaderObject(JsonElement root, string name, string filePath)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object) throw InvalidSession(filePath, $"v4 header has invalid {name}");
        return value.Clone();
    }

    /// <summary>
    /// 将 lane record 编码为 JSONL v4 mutation 行。
    /// </summary>
    /// <param name="record">待持久化的 lane record。</param>
    /// <returns>包含 kind、序号和 record 字段的单行 JSON。</returns>
    public static string SerializeRecord(LaneRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Id)) throw new ArgumentException("Record id is required.", nameof(record));
        if (record.Sequence <= 0) throw new ArgumentOutOfRangeException(nameof(record), "v4 record sequence must be positive.");
        if (string.IsNullOrWhiteSpace(record.Lane)) throw new ArgumentException("Record lane is required.", nameof(record));
        if (record.Timestamp < DateTimeOffset.UnixEpoch) throw new ArgumentOutOfRangeException(nameof(record));
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "record",
            ["id"] = record.Id,
            ["seq"] = record.Sequence,
            ["lane"] = record.Lane,
            ["timestamp"] = record.Timestamp.ToUnixTimeMilliseconds(),
            ["type"] = record.Type
        };
        switch (record)
        {
            case OperationStartedRecord started:
                fields["sourceLeafId"] = started.SourceLeafId;
                fields["intent"] = started.Intent is { } intent
                    ? intent
                    : started.OperationKind switch
                    {
                        "run" => new Dictionary<string, object?> { ["kind"] = "run", ["originalPrompt"] = Array.Empty<object>(), ["initialMessages"] = Array.Empty<object>() },
                        "compaction" => new Dictionary<string, object?> { ["kind"] = "compaction", ["resultEntryId"] = started.SourceLeafId ?? started.Id },
                        "navigation" => new Dictionary<string, object?> { ["kind"] = "navigation", ["targetId"] = null, ["summarize"] = false },
                        _ => throw new ArgumentException($"Unsupported operation kind '{started.OperationKind}'.", nameof(record))
                    };
                break;
            case OperationFinishedRecord finished:
                fields["runId"] = finished.RunId;
                fields["outcome"] = finished.Outcome;
                if (finished.Error is { } error) fields["error"] = error;
                break;
            case AbortRequestedRecord abort:
                fields["runId"] = abort.RunId;
                break;
            case StepAttemptRecord attempt:
                if (attempt.Attempt < 0 || attempt.Step is not ("assistant" or "branch_summary" or "compaction"))
                    throw new ArgumentException("Step attempt has an invalid step or attempt.", nameof(record));
                if (attempt.Step == "compaction" && attempt.CompactionReason is not ("manual" or "threshold" or "overflow"))
                    throw new ArgumentException("Compaction step attempt requires a valid compaction reason.", nameof(record));
                if (attempt.Step != "compaction" && attempt.CompactionReason is not null)
                    throw new ArgumentException("Only compaction step attempts may contain compactionReason.", nameof(record));
                fields["runId"] = attempt.RunId;
                fields["step"] = attempt.Step;
                fields["attempt"] = attempt.Attempt;
                fields["resultEntryId"] = attempt.ResultEntryId;
                if (attempt.CompactionReason is not null) fields["compactionReason"] = attempt.CompactionReason;
                break;
            case ToolStartedRecord tool:
                fields["runId"] = tool.RunId;
                fields["assistantEntryId"] = tool.AssistantEntryId;
                fields["toolIndex"] = tool.ToolIndex;
                fields["toolCallId"] = tool.ToolCallId;
                fields["toolName"] = tool.ToolName;
                fields["resultEntryId"] = tool.ResultEntryId;
                fields["effectiveArgs"] = tool.EffectiveArgs ?? JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), AgentCoreSessionJsonContext.Default.DictionaryStringObject);
                fields["replay"] = tool.Replay ?? "never";
                break;
            case QueueEnqueuedRecord queued:
                if (queued.Queue is not ("steer" or "followUp" or "nextRun"))
                    throw new ArgumentException("Queue record has an invalid queue.", nameof(record));
                if (queued.Queue is "steer" or "followUp" && string.IsNullOrWhiteSpace(queued.RunId))
                    throw new ArgumentException("Steer and followUp queue records require runId.", nameof(record));
                if (queued.Queue == "nextRun" && queued.RunId is not null)
                    throw new ArgumentException("nextRun queue records cannot contain runId.", nameof(record));
                fields["queue"] = queued.Queue;
                fields["target"] = queued.Target ?? JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?>
                    {
                        ["type"] = "custom",
                        ["id"] = queued.EntryId,
                        ["customType"] = "legacy_entry_reference",
                        ["data"] = new Dictionary<string, object?> { ["entryId"] = queued.EntryId }
                    },
                    AgentCoreSessionJsonContext.Default.DictionaryStringObject);
                if (queued.RunId is not null) fields["runId"] = queued.RunId;
                break;
            case QueueCancelledRecord cancelled:
                fields["entryId"] = cancelled.EntryId;
                if (cancelled.RunId is not null) fields["runId"] = cancelled.RunId;
                break;
            case WriteDeferredRecord deferred:
                fields["runId"] = deferred.RunId;
                fields["target"] = deferred.Target ?? JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?>
                    {
                        ["type"] = "custom",
                        ["id"] = deferred.EntryId,
                        ["customType"] = "legacy_entry_reference",
                        ["data"] = new Dictionary<string, object?> { ["entryId"] = deferred.EntryId }
                    },
                    AgentCoreSessionJsonContext.Default.DictionaryStringObject);
                break;
            case UsageRecord usage:
                if (usage.Cause is not ("assistant" or "compaction" or "branch_summary" or "deferred_fetch" or "tool" or "hook" or "adjustment"))
                    throw new ArgumentException("Usage record has an invalid cause.", nameof(record));
                fields["cause"] = usage.Cause;
                if (usage.RunId is not null) fields["runId"] = usage.RunId;
                if (usage.EntryId is not null) fields["entryId"] = usage.EntryId;
                if (usage.Attempt > 0) fields["attempt"] = usage.Attempt;
                if (usage.StopReason is not null) fields["stopReason"] = usage.StopReason;
                if (usage.ToolCallId is not null) fields["toolCallId"] = usage.ToolCallId;
                if (usage.Details is { } usageDetails) fields["details"] = usageDetails;
                var usageValue = usage.Usage;
                var usageFields = new Dictionary<string, object?>
                {
                    ["input"] = usageValue?.InputTokens ?? 0,
                    ["output"] = usageValue?.OutputTokens ?? 0,
                    ["cacheRead"] = usageValue?.CacheReadTokens ?? 0,
                    ["cacheWrite"] = usageValue?.CacheWriteTokens ?? 0,
                    ["totalTokens"] = usageValue?.TotalTokens ?? (usageValue?.InputTokens ?? 0) + (usageValue?.OutputTokens ?? 0),
                    ["cost"] = new Dictionary<string, object?>
                    {
                        ["input"] = usageValue?.Cost?.Input ?? 0m,
                        ["output"] = usageValue?.Cost?.Output ?? 0m,
                        ["cacheRead"] = usageValue?.Cost?.CacheRead ?? 0m,
                        ["cacheWrite"] = usageValue?.Cost?.CacheWrite ?? 0m,
                        ["total"] = usageValue?.Cost?.Total ?? 0m
                    }
                };
                if (usageValue?.CacheWrite1hTokens is { } cacheWrite1h) usageFields["cacheWrite1h"] = cacheWrite1h;
                if (usageValue?.ReasoningTokens is { } reasoning) usageFields["reasoning"] = reasoning;
                fields["usage"] = usageFields;
                break;
            default:
            {
                throw new InvalidOperationException($"Unsupported lane record type: {record.GetType().Name}.");
            }
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in fields)
            {
                writer.WritePropertyName(name);
                WriteJsonValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case IEnumerable<object?> array:
                writer.WriteStartArray();
                foreach (var item in array) WriteJsonValue(writer, item);
                writer.WriteEndArray();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long longNumber:
                writer.WriteNumberValue(longNumber);
                break;
            case decimal decimalNumber:
                writer.WriteNumberValue(decimalNumber);
                break;
            case IReadOnlyDictionary<string, object?> objectValue:
                writer.WriteStartObject();
                foreach (var (propertyName, propertyValue) in objectValue)
                {
                    writer.WritePropertyName(propertyName);
                    WriteJsonValue(writer, propertyValue);
                }

                writer.WriteEndObject();
                break;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string propertyName) continue;
                    writer.WritePropertyName(propertyName);
                    WriteJsonValue(writer, entry.Value);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable) WriteJsonValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    /// <summary>
    /// 从 JSONL v4 mutation 行解析 lane record。
    /// </summary>
    /// <param name="line">待解析的 JSONL 行。</param>
    /// <param name="filePath">源文件路径，用于错误定位。</param>
    /// <param name="lineNumber">行号。</param>
    /// <returns>解析后的 lane record。</returns>
    public static LaneRecord ParseRecord(string line, string filePath, int lineNumber)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("kind", out var kind) || kind.GetString() != "record")
                throw InvalidEntry(filePath, lineNumber, "is not a record mutation");

            var id = RequiredString(root, "id", filePath, lineNumber);
            var lane = RequiredString(root, "lane", filePath, lineNumber);
            var type = RequiredString(root, "type", filePath, lineNumber);
            var sequence = RequiredInt64(root, "seq", filePath, lineNumber);
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(RequiredInt64(root, "timestamp", filePath, lineNumber));
            ValidateRecordShape(root, type, filePath, lineNumber);
            string? OptionalString(string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
            int OptionalInt(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
            JsonElement? OptionalJson(string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.Clone() : null;
            string? TargetId(JsonElement? target) => target is { } element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
            Usage? ParseUsage()
            {
                if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
                    return null;
                static int? ReadInt(JsonElement value, string primary, string legacy)
                {
                    if (value.TryGetProperty(primary, out var primaryElement) && primaryElement.TryGetInt32(out var primaryValue)) return primaryValue;
                    if (value.TryGetProperty(legacy, out var legacyElement) && legacyElement.TryGetInt32(out var legacyValue)) return legacyValue;
                    return null;
                }
                var input = ReadInt(usageElement, "input", "inputTokens") ?? 0;
                var output = ReadInt(usageElement, "output", "outputTokens") ?? 0;
                var cacheRead = ReadInt(usageElement, "cacheRead", "cacheReadTokens");
                var cacheWrite = ReadInt(usageElement, "cacheWrite", "cacheWriteTokens");
                var cacheWrite1h = ReadInt(usageElement, "cacheWrite1h", "cacheWrite1hTokens");
                var reasoning = ReadInt(usageElement, "reasoning", "reasoningTokens");
                var total = usageElement.TryGetProperty("totalTokens", out var totalElement) && totalElement.TryGetInt32(out var totalValue) ? totalValue : (int?)null;
                UsageCost? cost = null;
                if (usageElement.TryGetProperty("cost", out var costElement) && costElement.ValueKind == JsonValueKind.Object)
                {
                    static decimal ReadDecimal(JsonElement value, string name) =>
                        value.TryGetProperty(name, out var property) && property.TryGetDecimal(out var number) && number >= 0 ? number : 0;
                    cost = new UsageCost(
                        ReadDecimal(costElement, "input"),
                        ReadDecimal(costElement, "output"),
                        ReadDecimal(costElement, "cacheRead"),
                        ReadDecimal(costElement, "cacheWrite"));
                }
                return new Usage(input, output, cacheRead, cacheWrite, Cost: cost) { CacheWrite1hTokens = cacheWrite1h, ReasoningTokens = reasoning, TotalTokens = total };
            }

            return type switch
            {
                "operation_started" => ParseOperationStarted(root, id, sequence, lane, timestamp, filePath, lineNumber),
                "operation_finished" => new OperationFinishedRecord(id, sequence, lane, timestamp, RequiredString(root, "runId", filePath, lineNumber), RequiredString(root, "outcome", filePath, lineNumber), OptionalJson("error")),
                "abort_requested" => new AbortRequestedRecord(id, sequence, lane, timestamp, RequiredString(root, "runId", filePath, lineNumber)),
                "step_attempt" => new StepAttemptRecord(id, sequence, lane, timestamp, RequiredString(root, "runId", filePath, lineNumber), RequiredString(root, "step", filePath, lineNumber), OptionalInt("attempt"), RequiredString(root, "resultEntryId", filePath, lineNumber), OptionalString("compactionReason")),
                "tool_started" => new ToolStartedRecord(id, sequence, lane, timestamp, RequiredString(root, "runId", filePath, lineNumber), RequiredString(root, "assistantEntryId", filePath, lineNumber), OptionalInt("toolIndex"), RequiredString(root, "toolCallId", filePath, lineNumber), RequiredString(root, "toolName", filePath, lineNumber), RequiredString(root, "resultEntryId", filePath, lineNumber), OptionalJson("effectiveArgs"), OptionalString("replay")),
                "queue_enqueued" => ParseQueueEnqueued(root, id, sequence, lane, timestamp, filePath, lineNumber, OptionalJson("target"), TargetId(OptionalJson("target"))),
                "queue_cancelled" => new QueueCancelledRecord(id, sequence, lane, timestamp, RequiredString(root, "entryId", filePath, lineNumber), OptionalString("runId")),
                "write_deferred" => ParseWriteDeferred(root, id, sequence, lane, timestamp, filePath, lineNumber, OptionalJson("target"), TargetId(OptionalJson("target"))),
                "usage" => new UsageRecord(id, sequence, lane, timestamp, RequiredString(root, "cause", filePath, lineNumber), OptionalString("runId"), OptionalString("entryId"), OptionalInt("attempt"), ParseUsage(), OptionalString("stopReason"), OptionalString("toolCallId"), OptionalJson("details")),
                _ => throw InvalidEntry(filePath, lineNumber, $"has unsupported record type '{type}'")
            };
        }
        catch (SessionException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw InvalidEntry(filePath, lineNumber, "is not valid JSON", ex);
        }
    }

    private static OperationStartedRecord ParseOperationStarted(JsonElement root, string id, long sequence, string lane, DateTimeOffset timestamp, string filePath, int lineNumber)
    {
        var sourceLeafId = root.TryGetProperty("sourceLeafId", out var source) && source.ValueKind != JsonValueKind.Null ? source.GetString() : null;
        if (root.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.Object)
        {
            var kind = intent.TryGetProperty("kind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String ? kindValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(kind)) throw InvalidEntry(filePath, lineNumber, "has invalid intent.kind");
            return new OperationStartedRecord(id, sequence, lane, timestamp, kind!, sourceLeafId, intent.Clone());
        }
        return new OperationStartedRecord(id, sequence, lane, timestamp, RequiredString(root, "operationKind", filePath, lineNumber), sourceLeafId);
    }

    /// <summary>
    /// 严格校验 pi v4 lane record 的公共字段以及各记录类型的 union 分支。
    /// </summary>
    /// <param name="root">record JSON 对象。</param>
    /// <param name="type">record 类型。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    private static void ValidateRecordShape(JsonElement root, string type, string filePath, int lineNumber)
    {
        static bool IsObject(JsonElement value) => value.ValueKind == JsonValueKind.Object;
        static bool IsString(JsonElement value) => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());
        static JsonElement Required(JsonElement value, string name, string path, int line) =>
            value.TryGetProperty(name, out var property) ? property : throw InvalidEntry(path, line, $"is missing {name}");
        static string RequiredText(JsonElement value, string name, string path, int line)
        {
            var property = Required(value, name, path, line);
            if (!IsString(property)) throw InvalidEntry(path, line, $"has invalid {name}");
            return property.GetString()!;
        }
        static void OptionalText(JsonElement value, string name, string path, int line)
        {
            if (value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null && !IsString(property))
                throw InvalidEntry(path, line, $"has invalid {name}");
        }
        static int RequiredNonNegativeInt(JsonElement value, string name, string path, int line)
        {
            var property = Required(value, name, path, line);
            if (!property.TryGetInt32(out var number) || number < 0) throw InvalidEntry(path, line, $"has invalid {name}");
            return number;
        }
        static void ValidateJsonValue(JsonElement value, string name, string path, int line)
        {
            if (value.ValueKind == JsonValueKind.Undefined)
                throw InvalidEntry(path, line, $"has invalid {name}");
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray()) ValidateJsonValue(item, name, path, line);
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject()) ValidateJsonValue(property.Value, name, path, line);
            }
        }
        static void ValidateMessage(JsonElement value, string name, string path, int line)
        {
            if (!IsObject(value)) throw InvalidEntry(path, line, $"has invalid {name}");
            if (!value.TryGetProperty("role", out var role) || !IsString(role) ||
                role.GetString() is not ("user" or "assistant" or "toolResult"))
                throw InvalidEntry(path, line, $"has invalid {name}.role");
            if (!value.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw InvalidEntry(path, line, $"has invalid {name}.content");
            foreach (var block in content.EnumerateArray()) ValidateJsonValue(block, $"{name}.content", path, line);
        }
        static void ValidateUsage(JsonElement value, string name, string path, int line)
        {
            if (!IsObject(value)) throw InvalidEntry(path, line, $"has invalid {name}");
            foreach (var tokenField in new[] { "input", "output", "cacheRead", "cacheWrite", "totalTokens" })
            {
                if (!value.TryGetProperty(tokenField, out var token) || !token.TryGetInt32(out var count) || count < 0)
                    throw InvalidEntry(path, line, $"has invalid {name}.{tokenField}");
            }
            foreach (var optionalTokenField in new[] { "cacheWrite1h", "reasoning" })
            {
                if (value.TryGetProperty(optionalTokenField, out var token) &&
                    (!token.TryGetInt32(out var count) || count < 0))
                    throw InvalidEntry(path, line, $"has invalid {name}.{optionalTokenField}");
            }
            var cost = Required(value, "cost", path, line);
            if (!IsObject(cost)) throw InvalidEntry(path, line, $"has invalid {name}.cost");
            foreach (var costField in new[] { "input", "output", "cacheRead", "cacheWrite", "total" })
            {
                if (!cost.TryGetProperty(costField, out var amount) || !amount.TryGetDecimal(out var number) || number < 0)
                    throw InvalidEntry(path, line, $"has invalid {name}.cost.{costField}");
            }
        }
        static void ValidateProvisionedEntry(JsonElement target, string name, string path, int line)
        {
            if (!IsObject(target)) throw InvalidEntry(path, line, $"has invalid {name}");
            if (target.TryGetProperty("parentId", out _) || target.TryGetProperty("seq", out _) || target.TryGetProperty("timestamp", out _))
                throw InvalidEntry(path, line, $"has invalid provisioned {name} metadata");
            var targetType = RequiredText(target, "type", path, line);
            if (targetType is not ("message" or "model_change" or "thinking_level_change" or "active_tools_change" or "compaction" or "branch_summary" or "custom"))
                throw InvalidEntry(path, line, $"has invalid {name}.type");
            RequiredText(target, "id", path, line);
            switch (targetType)
            {
                case "message":
                    ValidateMessage(Required(target, "message", path, line), $"{name}.message", path, line);
                    break;
                case "model_change":
                    RequiredText(target, "provider", path, line);
                    RequiredText(target, "modelId", path, line);
                    break;
                case "thinking_level_change":
                    RequiredText(target, "thinkingLevel", path, line);
                    break;
                case "active_tools_change":
                    if (Required(target, "activeToolNames", path, line).ValueKind != JsonValueKind.Array)
                        throw InvalidEntry(path, line, $"has invalid {name}.activeToolNames");
                    break;
                case "compaction":
                    RequiredText(target, "summary", path, line);
                    if (Required(target, "retainedTail", path, line).ValueKind != JsonValueKind.Array)
                        throw InvalidEntry(path, line, $"has invalid {name}.retainedTail");
                    RequiredNonNegativeInt(target, "tokensBefore", path, line);
                    if (target.TryGetProperty("details", out var details)) ValidateJsonValue(details, $"{name}.details", path, line);
                    if (target.TryGetProperty("usage", out var usage)) ValidateUsage(usage, $"{name}.usage", path, line);
                    break;
                case "branch_summary":
                    RequiredText(target, "fromId", path, line);
                    RequiredText(target, "summary", path, line);
                    if (target.TryGetProperty("details", out var branchDetails)) ValidateJsonValue(branchDetails, $"{name}.details", path, line);
                    if (target.TryGetProperty("usage", out var branchUsage)) ValidateUsage(branchUsage, $"{name}.usage", path, line);
                    break;
                case "custom":
                    RequiredText(target, "customType", path, line);
                    if (target.TryGetProperty("data", out var data)) ValidateJsonValue(data, $"{name}.data", path, line);
                    break;
            }
        }

        switch (type)
        {
            case "operation_started":
                if (root.TryGetProperty("intent", out var intent))
                {
                    ValidateOperationIntent(intent, filePath, lineNumber);
                    if (!root.TryGetProperty("sourceLeafId", out var sourceLeaf) ||
                        (sourceLeaf.ValueKind != JsonValueKind.Null && !IsString(sourceLeaf)))
                        throw InvalidEntry(filePath, lineNumber, "is missing or has invalid sourceLeafId");
                }
                else RequiredText(root, "operationKind", filePath, lineNumber);
                if (root.TryGetProperty("sourceLeafId", out var source) && source.ValueKind != JsonValueKind.Null && !IsString(source))
                    throw InvalidEntry(filePath, lineNumber, "has invalid sourceLeafId");
                break;
            case "operation_finished":
                RequiredText(root, "runId", filePath, lineNumber);
                var outcome = RequiredText(root, "outcome", filePath, lineNumber);
                if (outcome is not ("completed" or "aborted" or "failed" or "declined"))
                    throw InvalidEntry(filePath, lineNumber, "has invalid outcome");
                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                {
                    if (!IsObject(error)) throw InvalidEntry(filePath, lineNumber, "has invalid error");
                    RequiredText(error, "code", filePath, lineNumber);
                    RequiredText(error, "message", filePath, lineNumber);
                }
                break;
            case "abort_requested":
                RequiredText(root, "runId", filePath, lineNumber);
                break;
            case "step_attempt":
                RequiredText(root, "runId", filePath, lineNumber);
                var step = RequiredText(root, "step", filePath, lineNumber);
                if (step is not ("assistant" or "branch_summary" or "compaction"))
                    throw InvalidEntry(filePath, lineNumber, "has invalid step");
                RequiredNonNegativeInt(root, "attempt", filePath, lineNumber);
                RequiredText(root, "resultEntryId", filePath, lineNumber);
                if (step == "compaction")
                {
                    var reason = RequiredText(root, "compactionReason", filePath, lineNumber);
                    if (reason is not ("manual" or "threshold" or "overflow"))
                        throw InvalidEntry(filePath, lineNumber, "has invalid compactionReason");
                }
                else if (root.TryGetProperty("compactionReason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                    throw InvalidEntry(filePath, lineNumber, "has invalid compactionReason");
                break;
            case "tool_started":
                RequiredText(root, "runId", filePath, lineNumber);
                RequiredText(root, "assistantEntryId", filePath, lineNumber);
                RequiredNonNegativeInt(root, "toolIndex", filePath, lineNumber);
                RequiredText(root, "toolCallId", filePath, lineNumber);
                RequiredText(root, "toolName", filePath, lineNumber);
                RequiredText(root, "resultEntryId", filePath, lineNumber);
                var effectiveArgs = Required(root, "effectiveArgs", filePath, lineNumber);
                if (!IsObject(effectiveArgs)) throw InvalidEntry(filePath, lineNumber, "has invalid effectiveArgs");
                var replay = RequiredText(root, "replay", filePath, lineNumber);
                if (replay is not ("never" or "safe")) throw InvalidEntry(filePath, lineNumber, "has invalid replay");
                break;
            case "queue_enqueued":
                var queue = RequiredText(root, "queue", filePath, lineNumber);
                if (queue is not ("steer" or "followUp" or "nextRun")) throw InvalidEntry(filePath, lineNumber, "has invalid queue");
                if (root.TryGetProperty("target", out var target))
                    ValidateProvisionedEntry(target, "target", filePath, lineNumber);
                else
                    RequireStringProperty(root, "entryId", filePath, lineNumber);
                if (queue is "steer" or "followUp") RequiredText(root, "runId", filePath, lineNumber);
                else if (root.TryGetProperty("runId", out var nextRunId) && nextRunId.ValueKind != JsonValueKind.Null)
                    throw InvalidEntry(filePath, lineNumber, "nextRun queue cannot contain runId");
                break;
            case "queue_cancelled":
                RequiredText(root, "entryId", filePath, lineNumber);
                OptionalText(root, "runId", filePath, lineNumber);
                break;
            case "write_deferred":
                RequiredText(root, "runId", filePath, lineNumber);
                if (root.TryGetProperty("target", out var deferredTarget))
                    ValidateProvisionedEntry(deferredTarget, "target", filePath, lineNumber);
                else
                    RequireStringProperty(root, "entryId", filePath, lineNumber);
                break;
            case "usage":
                var cause = RequiredText(root, "cause", filePath, lineNumber);
                ValidateRecordUsage(root, cause, filePath, lineNumber);
                break;
            default:
                throw InvalidEntry(filePath, lineNumber, $"has unsupported record type '{type}'");
        }
    }

    /// <summary>
    /// 校验 operation_started 的 run、compaction、navigation 三种 intent 联合分支。
    /// </summary>
    /// <param name="intent">intent JSON 对象。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    private static void ValidateOperationIntent(JsonElement intent, string filePath, int lineNumber)
    {
        if (intent.ValueKind != JsonValueKind.Object || !intent.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            throw InvalidEntry(filePath, lineNumber, "has invalid intent");
        switch (kind.GetString())
        {
            case "run":
                var originalPrompt = RequireArrayProperty(intent, "originalPrompt", filePath, lineNumber);
                foreach (var message in originalPrompt.EnumerateArray()) ValidateAgentMessageShape(message, "originalPrompt", filePath, lineNumber);
                var initialMessages = RequireArrayProperty(intent, "initialMessages", filePath, lineNumber);
                foreach (var entry in initialMessages.EnumerateArray()) ValidateProvisionedEntryShape(entry, "initialMessages", filePath, lineNumber);
                OptionalStringProperty(intent, "systemPromptOverride", filePath, lineNumber);
                if (intent.TryGetProperty("resumeData", out var resumeData))
                {
                    OptionalObjectProperty(intent, "resumeData", filePath, lineNumber);
                    ValidateJsonValueShape(resumeData, "resumeData", filePath, lineNumber);
                }
                break;
            case "compaction":
                RequireStringProperty(intent, "resultEntryId", filePath, lineNumber);
                OptionalStringProperty(intent, "customInstructions", filePath, lineNumber);
                break;
            case "navigation":
                if (!intent.TryGetProperty("targetId", out _))
                    throw InvalidEntry(filePath, lineNumber, "is missing targetId");
                RequireNullableStringProperty(intent, "targetId", filePath, lineNumber);
                RequireBooleanProperty(intent, "summarize", filePath, lineNumber);
                OptionalStringProperty(intent, "customInstructions", filePath, lineNumber);
                OptionalStringProperty(intent, "label", filePath, lineNumber);
                OptionalStringProperty(intent, "summaryEntryId", filePath, lineNumber);
                break;
            default:
                throw InvalidEntry(filePath, lineNumber, "has invalid intent.kind");
        }
    }

    /// <summary>校验 JSON 值递归结构，拒绝 Undefined 等非 JSON 值。</summary>
    /// <param name="value">待校验的 JSON 值。</param>
    /// <param name="name">字段名称。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    private static void ValidateJsonValueShape(JsonElement value, string name, string filePath, int lineNumber)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}");
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) ValidateJsonValueShape(item, name, filePath, lineNumber);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject()) ValidateJsonValueShape(property.Value, name, filePath, lineNumber);
        }
    }

    /// <summary>校验 operation intent 中的 AgentMessage 最小结构。</summary>
    /// <param name="value">消息 JSON 值。</param>
    /// <param name="name">字段名称。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    private static void ValidateAgentMessageShape(JsonElement value, string name, string filePath, int lineNumber)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("role", out var role) ||
            role.ValueKind != JsonValueKind.String)
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}");

        var roleName = role.GetString();
        switch (roleName)
        {
            case "user":
                ValidateMessageContent(value, name, filePath, lineNumber, allowText: true, "text", "image");
                ValidateOptionalMessageTimestamp(value, name, filePath, lineNumber);
                break;
            case "assistant":
                ValidateMessageContent(value, name, filePath, lineNumber, allowText: false, "text", "thinking", "toolCall");
                ValidateOptionalMessageString(value, "api", name, filePath, lineNumber);
                ValidateOptionalMessageString(value, "provider", name, filePath, lineNumber);
                ValidateOptionalMessageString(value, "model", name, filePath, lineNumber);
                ValidateOptionalMessageString(value, "responseModel", name, filePath, lineNumber);
                ValidateOptionalMessageString(value, "responseId", name, filePath, lineNumber);
                ValidateOptionalMessageString(value, "rawStopReason", name, filePath, lineNumber);
                ValidateOptionalMessageString(value, "errorMessage", name, filePath, lineNumber);
                ValidateOptionalMessageBoolean(value, "endTurn", name, filePath, lineNumber);
                if (value.TryGetProperty("stopReason", out var stopReason) &&
                    (stopReason.ValueKind != JsonValueKind.String || stopReason.GetString() is not ("stop" or "length" or "toolUse" or "error" or "aborted" or "deferred")))
                    throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.stopReason");
                if (value.TryGetProperty("usage", out var usage)) ValidateMessageUsage(usage, $"{name}.usage", filePath, lineNumber);
                if (value.TryGetProperty("deferred", out var deferred) && deferred.ValueKind != JsonValueKind.Null)
                    ValidateDeferredHandleShape(deferred, $"{name}.deferred", filePath, lineNumber);
                if (value.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.ValueKind != JsonValueKind.Null && diagnostics.ValueKind != JsonValueKind.Array)
                    throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.diagnostics");
                ValidateOptionalMessageTimestamp(value, name, filePath, lineNumber);
                break;
            case "toolResult":
                ValidateMessageContent(value, name, filePath, lineNumber, allowText: false, "text", "image");
                ValidateOptionalMessageString(value, "toolCallId", name, filePath, lineNumber);
                ValidateOptionalMessageString(value, "toolName", name, filePath, lineNumber);
                ValidateOptionalMessageBoolean(value, "isError", name, filePath, lineNumber);
                if (value.TryGetProperty("usage", out var toolUsage)) ValidateMessageUsage(toolUsage, $"{name}.usage", filePath, lineNumber);
                if (value.TryGetProperty("addedToolNames", out var addedTools) && addedTools.ValueKind != JsonValueKind.Null)
                {
                    if (addedTools.ValueKind != JsonValueKind.Array || addedTools.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                        throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.addedToolNames");
                }
                ValidateOptionalMessageTimestamp(value, name, filePath, lineNumber);
                break;
            case "bashExecution":
                RequireMessageString(value, "command", name, filePath, lineNumber);
                RequireMessageString(value, "output", name, filePath, lineNumber);
                ValidateOptionalMessageInteger(value, "exitCode", name, filePath, lineNumber);
                RequireMessageBoolean(value, "cancelled", name, filePath, lineNumber);
                RequireMessageBoolean(value, "truncated", name, filePath, lineNumber);
                ValidateOptionalMessageString(value, "fullOutputPath", name, filePath, lineNumber);
                ValidateOptionalMessageBoolean(value, "excludeFromContext", name, filePath, lineNumber);
                ValidateOptionalMessageTimestamp(value, name, filePath, lineNumber);
                break;
            case "custom":
                RequireMessageString(value, "customType", name, filePath, lineNumber);
                ValidateMessageContent(value, name, filePath, lineNumber, allowText: true, "text", "image");
                RequireMessageBoolean(value, "display", name, filePath, lineNumber);
                ValidateOptionalMessageTimestamp(value, name, filePath, lineNumber);
                if (value.TryGetProperty("details", out var customDetails)) ValidateJsonValueShape(customDetails, $"{name}.details", filePath, lineNumber);
                break;
            case "branchSummary":
                RequireMessageString(value, "summary", name, filePath, lineNumber);
                RequireMessageString(value, "fromId", name, filePath, lineNumber);
                ValidateOptionalMessageTimestamp(value, name, filePath, lineNumber);
                break;
            case "compactionSummary":
                RequireMessageString(value, "summary", name, filePath, lineNumber);
                RequireMessageNonNegativeInteger(value, "tokensBefore", name, filePath, lineNumber);
                ValidateOptionalMessageTimestamp(value, name, filePath, lineNumber);
                break;
            default:
                throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.role");
        }
    }

    private static void ValidateMessageContent(JsonElement value, string name, string filePath, int lineNumber, bool allowText, params string[] allowedTypes)
    {
        if (!value.TryGetProperty("content", out var content))
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.content");
        if (allowText && content.ValueKind == JsonValueKind.String) return;
        if (content.ValueKind != JsonValueKind.Array)
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.content");

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || !allowedTypes.Contains(type.GetString(), StringComparer.Ordinal))
                throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.content");
            switch (type.GetString())
            {
                case "text": RequireMessageString(block, "text", $"{name}.content", filePath, lineNumber); ValidateOptionalMessageString(block, "textSignature", name, filePath, lineNumber); break;
                case "thinking": RequireMessageString(block, "thinking", $"{name}.content", filePath, lineNumber); ValidateOptionalMessageString(block, "thinkingSignature", name, filePath, lineNumber); ValidateOptionalMessageBoolean(block, "redacted", name, filePath, lineNumber); break;
                case "image": RequireMessageString(block, "data", $"{name}.content", filePath, lineNumber); RequireMessageString(block, "mimeType", $"{name}.content", filePath, lineNumber); break;
                case "toolCall":
                    RequireMessageString(block, "id", $"{name}.content", filePath, lineNumber);
                    RequireMessageString(block, "name", $"{name}.content", filePath, lineNumber);
                    if (!block.TryGetProperty("arguments", out var arguments) || arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.content.arguments");
                    ValidateOptionalMessageString(block, "thoughtSignature", name, filePath, lineNumber);
                    break;
            }
        }
    }

    private static void ValidateOptionalMessageTimestamp(JsonElement value, string name, string filePath, int lineNumber)
    {
        if (!value.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind == JsonValueKind.Null) return;
        if (timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var unix) && unix >= 0) return;
        if (timestamp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(timestamp.GetString(), out _)) return;
        throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.timestamp");
    }

    private static void ValidateOptionalMessageString(JsonElement value, string propertyName, string name, string filePath, int lineNumber)
    {
        if (value.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null && (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString())))
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.{propertyName}");
    }

    private static void ValidateOptionalMessageBoolean(JsonElement value, string propertyName, string name, string filePath, int lineNumber)
    {
        if (value.TryGetProperty(propertyName, out var property) && property.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.{propertyName}");
    }

    private static void ValidateOptionalMessageInteger(JsonElement value, string propertyName, string name, string filePath, int lineNumber)
    {
        if (value.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null && (!property.TryGetInt32(out var number) || number < 0))
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.{propertyName}");
    }

    private static void RequireMessageString(JsonElement value, string propertyName, string name, string filePath, int lineNumber)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw InvalidEntry(filePath, lineNumber, $"is missing or invalid {name}.{propertyName}");
    }

    private static void RequireMessageBoolean(JsonElement value, string propertyName, string name, string filePath, int lineNumber)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidEntry(filePath, lineNumber, $"is missing or invalid {name}.{propertyName}");
    }

    private static void RequireMessageNonNegativeInteger(JsonElement value, string propertyName, string name, string filePath, int lineNumber)
    {
        if (!value.TryGetProperty(propertyName, out var property) || !property.TryGetInt32(out var number) || number < 0)
            throw InvalidEntry(filePath, lineNumber, $"is missing or invalid {name}.{propertyName}");
    }

    private static void ValidateMessageUsage(JsonElement value, string name, string filePath, int lineNumber)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidEntry(filePath, lineNumber, $"has invalid {name}");
        foreach (var token in new[] { "input", "output", "cacheRead", "cacheWrite", "totalTokens" })
            if (!value.TryGetProperty(token, out var item) || !item.TryGetInt32(out var count) || count < 0)
                throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.{token}");
        foreach (var token in new[] { "cacheWrite1h", "reasoning" })
            if (value.TryGetProperty(token, out var item) && item.ValueKind != JsonValueKind.Null && (!item.TryGetInt32(out var count) || count < 0))
                throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.{token}");
        if (!value.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.cost");
        foreach (var field in new[] { "input", "output", "cacheRead", "cacheWrite", "total" })
            if (!cost.TryGetProperty(field, out var amount) || !amount.TryGetDecimal(out var number) || number < 0)
                throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.cost.{field}");
    }

    private static void ValidateDeferredHandleShape(JsonElement value, string name, string filePath, int lineNumber)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidEntry(filePath, lineNumber, $"has invalid {name}");
        foreach (var property in new[] { "provider", "modelId", "api", "id" }) RequireMessageString(value, property, name, filePath, lineNumber);
        ValidateOptionalMessageInteger(value, "pollAfterMs", name, filePath, lineNumber);
        if (value.TryGetProperty("expiresAt", out var expiresAt) && expiresAt.ValueKind != JsonValueKind.Null &&
            !((expiresAt.ValueKind == JsonValueKind.Number && expiresAt.TryGetInt64(out var unix) && unix >= 0) ||
              (expiresAt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(expiresAt.GetString(), out _))))
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.expiresAt");
        if (value.TryGetProperty("data", out var data)) ValidateJsonValueShape(data, $"{name}.data", filePath, lineNumber);
    }

    /// <summary>校验 ProvisionedEntry 的 discriminated union 字段。</summary>
    /// <param name="value">ProvisionedEntry JSON 值。</param>
    /// <param name="name">字段名称。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    private static void ValidateProvisionedEntryShape(JsonElement value, string name, string filePath, int lineNumber)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}");
        if (value.TryGetProperty("parentId", out _) || value.TryGetProperty("seq", out _) || value.TryGetProperty("timestamp", out _))
            throw InvalidEntry(filePath, lineNumber, $"has invalid provisioned {name} metadata");
        if (!value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.type");
        if (!value.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.id");
        switch (type.GetString())
        {
            case "message":
                if (!value.TryGetProperty("message", out var message)) throw InvalidEntry(filePath, lineNumber, $"is missing {name}.message");
                ValidateAgentMessageShape(message, $"{name}.message", filePath, lineNumber);
                break;
            case "model_change":
                RequireStringProperty(value, "provider", filePath, lineNumber);
                RequireStringProperty(value, "modelId", filePath, lineNumber);
                break;
            case "thinking_level_change":
                RequireStringProperty(value, "thinkingLevel", filePath, lineNumber);
                break;
            case "active_tools_change":
                if (!value.TryGetProperty("activeToolNames", out var tools) || tools.ValueKind != JsonValueKind.Array)
                    throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.activeToolNames");
                break;
            case "compaction":
                RequireStringProperty(value, "summary", filePath, lineNumber);
                if (!value.TryGetProperty("retainedTail", out var tail) || tail.ValueKind != JsonValueKind.Array)
                    throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.retainedTail");
                RequirePositiveOrZeroIntProperty(value, "tokensBefore", filePath, lineNumber);
                if (value.TryGetProperty("details", out var details)) ValidateJsonValueShape(details, $"{name}.details", filePath, lineNumber);
                break;
            case "branch_summary":
                RequireStringProperty(value, "fromId", filePath, lineNumber);
                RequireStringProperty(value, "summary", filePath, lineNumber);
                if (value.TryGetProperty("details", out var branchDetails)) ValidateJsonValueShape(branchDetails, $"{name}.details", filePath, lineNumber);
                break;
            case "custom":
                RequireStringProperty(value, "customType", filePath, lineNumber);
                if (value.TryGetProperty("data", out var data)) ValidateJsonValueShape(data, $"{name}.data", filePath, lineNumber);
                break;
            default:
                throw InvalidEntry(filePath, lineNumber, $"has invalid {name}.type");
        }
    }

    /// <summary>
    /// 校验 usage record 的 cause-specific 必填字段和 Usage 对象。
    /// </summary>
    /// <param name="root">record JSON 对象。</param>
    /// <param name="cause">usage 原因。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    private static void ValidateRecordUsage(JsonElement root, string cause, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw InvalidEntry(filePath, lineNumber, "is missing usage");
        foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite", "totalTokens" })
        {
            if (!usage.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number < 0)
                throw InvalidEntry(filePath, lineNumber, $"has invalid usage.{name}");
        }
        foreach (var name in new[] { "cacheWrite1h", "reasoning" })
        {
            if (usage.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null &&
                (!value.TryGetInt32(out var number) || number < 0))
                throw InvalidEntry(filePath, lineNumber, $"has invalid usage.{name}");
        }
        if (!usage.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
            throw InvalidEntry(filePath, lineNumber, "is missing usage.cost");
        foreach (var name in new[] { "input", "output", "cacheRead", "cacheWrite", "total" })
        {
            if (!cost.TryGetProperty(name, out var value) || !value.TryGetDecimal(out var number) || number < 0)
                throw InvalidEntry(filePath, lineNumber, $"has invalid usage.cost.{name}");
        }
        switch (cause)
        {
            case "assistant":
            case "compaction":
            case "branch_summary":
            case "deferred_fetch":
                RequireStringProperty(root, "runId", filePath, lineNumber);
                RequireStringProperty(root, "entryId", filePath, lineNumber);
                RequirePositiveIntProperty(root, "attempt", filePath, lineNumber);
                var stopReason = RequireStringProperty(root, "stopReason", filePath, lineNumber);
                if (stopReason is not ("stop" or "length" or "toolUse" or "error" or "aborted" or "deferred"))
                    throw InvalidEntry(filePath, lineNumber, "has invalid stopReason");
                break;
            case "tool":
                RequireStringProperty(root, "runId", filePath, lineNumber);
                RequireStringProperty(root, "entryId", filePath, lineNumber);
                RequireStringProperty(root, "toolCallId", filePath, lineNumber);
                break;
            case "hook":
                RequireStringProperty(root, "runId", filePath, lineNumber);
                RequireStringProperty(root, "entryId", filePath, lineNumber);
                break;
            case "adjustment":
                OptionalStringProperty(root, "runId", filePath, lineNumber);
                OptionalStringProperty(root, "entryId", filePath, lineNumber);
                if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Undefined)
                    throw InvalidEntry(filePath, lineNumber, "has invalid details");
                break;
            default:
                throw InvalidEntry(filePath, lineNumber, "has invalid cause");
        }
    }

    private static JsonElement RequireArrayProperty(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw InvalidEntry(filePath, lineNumber, $"is missing or invalid {name}");
        return value;
    }

    private static void RequireBooleanProperty(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidEntry(filePath, lineNumber, $"is missing or invalid {name}");
    }

    private static string RequireStringProperty(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw InvalidEntry(filePath, lineNumber, $"is missing or invalid {name}");
        return value.GetString()!;
    }

    private static void RequireNullableStringProperty(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}");
    }

    private static void OptionalStringProperty(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.String)
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}");
    }

    private static void OptionalObjectProperty(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Object)
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}");
    }

    private static void RequirePositiveIntProperty(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number <= 0)
            throw InvalidEntry(filePath, lineNumber, $"is missing or invalid {name}");
    }

    /// <summary>校验 JSON 对象中的非负整数属性。</summary>
    /// <param name="root">待校验的 JSON 对象。</param>
    /// <param name="name">属性名称。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    private static void RequirePositiveOrZeroIntProperty(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number < 0)
            throw InvalidEntry(filePath, lineNumber, $"is missing or invalid {name}");
    }

    private static QueueEnqueuedRecord ParseQueueEnqueued(JsonElement root, string id, long sequence, string lane, DateTimeOffset timestamp, string filePath, int lineNumber, JsonElement? target, string? targetId)
    {
        var entryId = targetId ?? RequiredString(root, "entryId", filePath, lineNumber);
        return new QueueEnqueuedRecord(id, sequence, lane, timestamp, RequiredString(root, "queue", filePath, lineNumber), entryId, root.TryGetProperty("runId", out var runId) && runId.ValueKind != JsonValueKind.Null ? runId.GetString() : null, target);
    }

    private static WriteDeferredRecord ParseWriteDeferred(JsonElement root, string id, long sequence, string lane, DateTimeOffset timestamp, string filePath, int lineNumber, JsonElement? target, string? targetId)
    {
        var entryId = targetId ?? RequiredString(root, "entryId", filePath, lineNumber);
        return new WriteDeferredRecord(id, sequence, lane, timestamp, RequiredString(root, "runId", filePath, lineNumber), entryId, target);
    }

    private static string RequiredString(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw InvalidEntry(filePath, lineNumber, $"is missing {name}");
        return value.GetString()!;
    }

    private static long RequiredInt64(JsonElement root, string name, string filePath, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt64(out var result) || result < 0)
            throw InvalidEntry(filePath, lineNumber, $"has invalid {name}");
        return result;
    }

    public static SessionTreeEntry ParseEntry(string line, string filePath, int lineNumber)
    {
        bool isV4Mutation = false;
        try
        {
            using var shape = JsonDocument.Parse(line);
            isV4Mutation = shape.RootElement.ValueKind == JsonValueKind.Object &&
                           shape.RootElement.TryGetProperty("kind", out var kind) &&
                           kind.ValueKind == JsonValueKind.String && kind.GetString() == "entry";
            if (isV4Mutation)
            {
                if (!shape.RootElement.TryGetProperty("seq", out var sequence) || !sequence.TryGetInt64(out var seq) || seq <= 0)
                    throw InvalidEntry(filePath, lineNumber, "has invalid seq");
                if (!shape.RootElement.TryGetProperty("parentId", out _))
                    throw InvalidEntry(filePath, lineNumber, "is missing parentId");
                if (!shape.RootElement.TryGetProperty("timestamp", out var numericTimestamp) || numericTimestamp.ValueKind != JsonValueKind.Number || !numericTimestamp.TryGetInt64(out var timestamp) || timestamp < 0)
                    throw InvalidEntry(filePath, lineNumber, "has invalid timestamp");
                ValidateEntryMutationShape(shape.RootElement, filePath, lineNumber);
            }
        }
        catch (SessionException) { throw; }
        catch (JsonException ex) { throw InvalidEntry(filePath, lineNumber, "is not valid JSON", ex); }

        // v4 mutation 将 timestamp 以 Unix 毫秒写入；转换成现有 DTO 使用的 ISO 文本
        try
        {
            using var raw = JsonDocument.Parse(line);
            if (raw.RootElement.TryGetProperty("timestamp", out var timestamp) &&
                timestamp.ValueKind == JsonValueKind.Number &&
                timestamp.TryGetInt64(out var unixMilliseconds))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(line);
                if (node is System.Text.Json.Nodes.JsonObject obj)
                {
                    obj["timestamp"] = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
                        .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    NormalizeNestedMessageTimestamps(obj, toUnixMilliseconds: false);
                    line = obj.ToJsonString();
                }
            }
        }
        catch (JsonException)
        {
            // 交由下方 DTO 解析路径统一报告 invalid_entry
        }

        JsonlSessionEntryDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(line, AgentCoreSessionJsonContext.Default.JsonlSessionEntryDto);
        }
        catch (JsonException ex)
        {
            throw InvalidEntry(filePath, lineNumber, "is not valid JSON", ex);
        }

        if (dto is null)
            throw InvalidEntry(filePath, lineNumber, "is not a valid session entry");
        if (string.IsNullOrWhiteSpace(dto.Type))
            throw InvalidEntry(filePath, lineNumber, "is missing entry type");
        if (string.IsNullOrWhiteSpace(dto.Id))
            throw InvalidEntry(filePath, lineNumber, "is missing entry id");
        if (dto.Timestamp == default)
            throw InvalidEntry(filePath, lineNumber, "is missing timestamp");

        SessionTreeEntry parsed = dto.Type switch
        {
            "message" when dto.Message is not null => new MessageSessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                ToMessage(dto.Message) ?? throw InvalidEntry(filePath, lineNumber, "has invalid message"),
                dto.Terminate == true),
            "thinking_level_change" when dto.ThinkingLevel is not null => new ThinkingLevelChangeSessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.ThinkingLevel),
            "model_change" when dto.Provider is not null && dto.ModelId is not null => new ModelChangeSessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.Provider,
                dto.ModelId),
            "active_tools_change" when dto.ActiveToolNames is not null => new ActiveToolsChangeSessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.ActiveToolNames.ToArray()),
            "compaction" when dto.Summary is not null && dto.TokensBefore is not null => new CompactionSessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.Summary,
                dto.FirstKeptEntryId ?? string.Empty,
                dto.TokensBefore.Value,
                dto.Details,
                dto.FromHook == true,
                ToRetainedTail(dto.RetainedTail, filePath, lineNumber),
                ToUsage(dto.Usage)),
            "custom" when dto.CustomType is not null => new CustomSessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.CustomType,
                dto.Data),
            "custom_message" when dto.CustomType is not null && dto.Content is not null => new CustomMessageSessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.CustomType,
                ToCustomMessageContent(dto.Content.Value, filePath, lineNumber),
                dto.Display == true,
                dto.Details),
            "label" when dto.TargetId is not null => new LabelSessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.TargetId,
                dto.Label),
            "session_info" => new SessionInfoEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.Name),
            "leaf" => new LeafSessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.TargetId),
            "branch_summary" when dto.FromId is not null && dto.Summary is not null => new BranchSummarySessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.FromId,
                dto.Summary,
                dto.Details,
                dto.FromHook == true,
                ToUsage(dto.Usage)),
            _ => throw InvalidEntry(filePath, lineNumber, "is not a supported session entry")
        };
        return (dto.Seq is > 0 ? parsed with { Sequence = dto.Seq.Value } : parsed) with { Lane = dto.Lane };
    }

    /// <summary>
    /// 校验 v4 entry mutation 的类型分支和必填字段，避免 DTO 的可空属性掩盖坏数据。
    /// </summary>
    /// <param name="root">entry JSON 对象。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">源文件行号。</param>
    private static void ValidateEntryMutationShape(JsonElement root, string filePath, int lineNumber)
    {
        static JsonElement Required(JsonElement value, string name, string path, int line) =>
            value.TryGetProperty(name, out var property) ? property : throw InvalidEntry(path, line, $"is missing {name}");
        static string Text(JsonElement value, string name, string path, int line)
        {
            var property = Required(value, name, path, line);
            if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
                throw InvalidEntry(path, line, $"has invalid {name}");
            return property.GetString()!;
        }
        static void NullableText(JsonElement value, string name, string path, int line)
        {
            if (value.TryGetProperty(name, out var property) && property.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw InvalidEntry(path, line, $"has invalid {name}");
        }
        static void Array(JsonElement value, string name, string path, int line)
        {
            if (Required(value, name, path, line).ValueKind != JsonValueKind.Array)
                throw InvalidEntry(path, line, $"has invalid {name}");
        }
        static void NonNegativeInt(JsonElement value, string name, string path, int line)
        {
            if (!Required(value, name, path, line).TryGetInt32(out var number) || number < 0)
                throw InvalidEntry(path, line, $"has invalid {name}");
        }

        var type = Text(root, "type", filePath, lineNumber);
        Text(root, "id", filePath, lineNumber);
        NullableText(root, "parentId", filePath, lineNumber);
        switch (type)
        {
            case "message":
                var message = Required(root, "message", filePath, lineNumber);
                ValidateAgentMessageShape(message, "message", filePath, lineNumber);
                break;
            case "thinking_level_change":
                Text(root, "thinkingLevel", filePath, lineNumber);
                break;
            case "model_change":
                Text(root, "provider", filePath, lineNumber);
                Text(root, "modelId", filePath, lineNumber);
                break;
            case "active_tools_change":
                Array(root, "activeToolNames", filePath, lineNumber);
                break;
            case "compaction":
                Text(root, "summary", filePath, lineNumber);
                NonNegativeInt(root, "tokensBefore", filePath, lineNumber);
                if (root.TryGetProperty("retainedTail", out var retainedTail))
                {
                    if (retainedTail.ValueKind != JsonValueKind.Array) throw InvalidEntry(filePath, lineNumber, "has invalid retainedTail");
                }
                else if (!root.TryGetProperty("firstKeptEntryId", out var legacyFirst) || legacyFirst.ValueKind != JsonValueKind.String)
                    throw InvalidEntry(filePath, lineNumber, "is missing retainedTail");
                break;
            case "branch_summary":
                Text(root, "fromId", filePath, lineNumber);
                Text(root, "summary", filePath, lineNumber);
                break;
            case "custom":
                Text(root, "customType", filePath, lineNumber);
                break;
            case "custom_message":
                Text(root, "customType", filePath, lineNumber);
                if (!root.TryGetProperty("content", out var customContent) || customContent.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
                    throw InvalidEntry(filePath, lineNumber, "is missing or invalid content");
                break;
            case "label":
                Text(root, "targetId", filePath, lineNumber);
                NullableText(root, "label", filePath, lineNumber);
                break;
            case "session_info":
                NullableText(root, "name", filePath, lineNumber);
                break;
            case "leaf":
                NullableText(root, "targetId", filePath, lineNumber);
                break;
            default:
                throw InvalidEntry(filePath, lineNumber, $"has unsupported session entry type '{type}'");
        }
    }

    private static JsonlSessionEntryDto ToDto(SessionTreeEntry entry) =>
        entry switch
        {
            MessageSessionEntry message => new JsonlSessionEntryDto
            {
                Type = message.Type,
                Id = message.Id,
                Seq = message.Sequence > 0 ? message.Sequence : null,
                Lane = message.Lane,
                ParentId = message.ParentId,
                Timestamp = message.Timestamp,
                Message = FromMessage(message.Message),
                Terminate = message.Terminate ? true : null
            },
            ThinkingLevelChangeSessionEntry thinking => new JsonlSessionEntryDto
            {
                Type = thinking.Type,
                Id = thinking.Id,
                Seq = thinking.Sequence > 0 ? thinking.Sequence : null,
                Lane = thinking.Lane,
                ParentId = thinking.ParentId,
                Timestamp = thinking.Timestamp,
                ThinkingLevel = thinking.ThinkingLevel
            },
            ModelChangeSessionEntry model => new JsonlSessionEntryDto
            {
                Type = model.Type,
                Id = model.Id,
                Seq = model.Sequence > 0 ? model.Sequence : null,
                Lane = model.Lane,
                ParentId = model.ParentId,
                Timestamp = model.Timestamp,
                Provider = model.Provider,
                ModelId = model.ModelId
            },
            ActiveToolsChangeSessionEntry tools => new JsonlSessionEntryDto
            {
                Type = tools.Type,
                Id = tools.Id,
                Seq = tools.Sequence > 0 ? tools.Sequence : null,
                Lane = tools.Lane,
                ParentId = tools.ParentId,
                Timestamp = tools.Timestamp,
                ActiveToolNames = tools.ActiveToolNames.ToArray()
            },
            CompactionSessionEntry compaction => new JsonlSessionEntryDto
            {
                Type = compaction.Type,
                Id = compaction.Id,
                Seq = compaction.Sequence > 0 ? compaction.Sequence : null,
                Lane = compaction.Lane,
                ParentId = compaction.ParentId,
                Timestamp = compaction.Timestamp,
                Summary = compaction.Summary,
                FirstKeptEntryId = compaction.FirstKeptEntryId,
                TokensBefore = compaction.TokensBefore,
                Details = ToJsonElement(compaction.Details),
                FromHook = compaction.FromHook,
                RetainedTail = compaction.RetainedTail is null
                    ? null
                    : JsonSerializer.SerializeToElement(compaction.RetainedTail.Select(FromMessage).ToArray(), AgentCoreSessionJsonContext.Default.SessionMessageDtoArray),
                Usage = FromUsage(compaction.Usage)
            },
            CustomSessionEntry custom => new JsonlSessionEntryDto
            {
                Type = custom.Type,
                Id = custom.Id,
                Seq = custom.Sequence > 0 ? custom.Sequence : null,
                Lane = custom.Lane,
                ParentId = custom.ParentId,
                Timestamp = custom.Timestamp,
                CustomType = custom.CustomType,
                Data = ToJsonElement(custom.Data)
            },
            CustomMessageSessionEntry customMessage => new JsonlSessionEntryDto
            {
                Type = customMessage.Type,
                Id = customMessage.Id,
                Seq = customMessage.Sequence > 0 ? customMessage.Sequence : null,
                Lane = customMessage.Lane,
                ParentId = customMessage.ParentId,
                Timestamp = customMessage.Timestamp,
                CustomType = customMessage.CustomType,
                Content = FromCustomMessageContent(customMessage.Content),
                Display = customMessage.Display,
                Details = ToJsonElement(customMessage.Details)
            },
            LabelSessionEntry label => new JsonlSessionEntryDto
            {
                Type = label.Type,
                Id = label.Id,
                Seq = label.Sequence > 0 ? label.Sequence : null,
                Lane = label.Lane,
                ParentId = label.ParentId,
                Timestamp = label.Timestamp,
                TargetId = label.TargetId,
                Label = label.Label
            },
            SessionInfoEntry info => new JsonlSessionEntryDto
            {
                Type = info.Type,
                Id = info.Id,
                Seq = info.Sequence > 0 ? info.Sequence : null,
                Lane = info.Lane,
                ParentId = info.ParentId,
                Timestamp = info.Timestamp,
                Name = info.Name
            },
            LeafSessionEntry leaf => new JsonlSessionEntryDto
            {
                Type = leaf.Type,
                Id = leaf.Id,
                Seq = leaf.Sequence > 0 ? leaf.Sequence : null,
                Lane = leaf.Lane,
                ParentId = leaf.ParentId,
                Timestamp = leaf.Timestamp,
                TargetId = leaf.TargetId
            },
            BranchSummarySessionEntry summary => new JsonlSessionEntryDto
            {
                Type = summary.Type,
                Id = summary.Id,
                Seq = summary.Sequence > 0 ? summary.Sequence : null,
                Lane = summary.Lane,
                ParentId = summary.ParentId,
                Timestamp = summary.Timestamp,
                FromId = summary.FromId,
                Summary = summary.Summary,
                Details = ToJsonElement(summary.Details),
                FromHook = summary.FromHook,
                Usage = FromUsage(summary.Usage)
            },
            _ => throw new InvalidOperationException($"Unsupported session entry type: {entry.Type}")
        };

    private static SessionMessageDto FromMessage(ChatMessage message) =>
        message switch
        {
            UserMessage user => new SessionMessageDto
            {
                Role = "user",
                Content = SerializeMessageContent(user.Content),
                Timestamp = user.Timestamp
            },
            AssistantMessage assistant => new SessionMessageDto
            {
                Role = "assistant",
                Content = SerializeMessageContent(assistant.Content),
                Usage = FromUsage(assistant.Usage),
                Api = Normalize(assistant.Api),
                Provider = Normalize(assistant.Provider),
                Model = Normalize(assistant.Model),
                ResponseModel = Normalize(assistant.ResponseModel),
                ResponseId = Normalize(assistant.ResponseId),
                StopReason = ToWireStopReason(assistant.StopReason),
                RawStopReason = Normalize(assistant.RawStopReason),
                EndTurn = assistant.EndTurn,
                Diagnostics = ToJsonElement(assistant.Diagnostics),
                Deferred = ToDeferredHandleDto(assistant.Deferred),
                Timestamp = assistant.Timestamp,
                ErrorMessage = Normalize(assistant.ErrorMessage)
            },
            ToolResultMessage toolResult => new SessionMessageDto
            {
                Role = "toolResult",
                Content = SerializeMessageContent(toolResult.Content),
                ToolCallId = toolResult.ToolCallId,
                ToolName = Normalize(toolResult.ToolName),
                Details = ToJsonElement(toolResult.Details),
                Usage = FromUsage(toolResult.Usage),
                AddedToolNames = toolResult.AddedToolNames?.ToArray(),
                Timestamp = toolResult.Timestamp,
                IsError = toolResult.IsError
            },
            AgentBashExecutionMessage bash => new SessionMessageDto
            {
                Role = "bashExecution",
                Command = bash.Command,
                Output = bash.Output,
                ExitCode = bash.ExitCode,
                Cancelled = bash.Cancelled,
                Truncated = bash.Truncated,
                FullOutputPath = Normalize(bash.FullOutputPath),
                Timestamp = bash.Timestamp,
                ExcludeFromContext = bash.ExcludeFromContext
            },
            AgentCustomMessage custom => new SessionMessageDto
            {
                Role = "custom",
                CustomType = custom.CustomType,
                Content = SerializeMessageContent(custom.Content),
                Display = custom.Display,
                Details = ToJsonElement(custom.Details),
                Timestamp = custom.Timestamp
            },
            AgentBranchSummaryMessage summary => new SessionMessageDto
            {
                Role = "branchSummary",
                Summary = summary.Summary,
                FromId = summary.FromId,
                Timestamp = summary.Timestamp
            },
            AgentCompactionSummaryMessage summary => new SessionMessageDto
            {
                Role = "compactionSummary",
                Summary = summary.Summary,
                TokensBefore = summary.TokensBefore,
                Timestamp = summary.Timestamp
            },
            _ => new SessionMessageDto
            {
                Role = message.Role,
            }
        };

    private static ChatMessage? ToMessage(SessionMessageDto message)
    {
        var content = ToMessageContent(message.Content);

        return message.Role switch
        {
            "user" => new UserMessage(content) { Timestamp = message.Timestamp },
            "assistant" => new AssistantMessage(content)
            {
                Usage = ToUsage(message.Usage),
                Api = Normalize(message.Api),
                Provider = Normalize(message.Provider),
                Model = Normalize(message.Model),
                ResponseModel = Normalize(message.ResponseModel),
                ResponseId = Normalize(message.ResponseId),
                StopReason = FromWireStopReason(message.StopReason),
                RawStopReason = Normalize(message.RawStopReason),
                EndTurn = message.EndTurn,
                Diagnostics = ParseDiagnostics(message.Diagnostics),
                Deferred = ToDeferredHandle(message.Deferred),
                ErrorMessage = Normalize(message.ErrorMessage),
                Timestamp = message.Timestamp
            },
            "toolResult" when !string.IsNullOrWhiteSpace(message.ToolCallId) => new ToolResultMessage(
                message.ToolCallId,
                content,
                message.IsError == true)
            {
                ToolName = Normalize(message.ToolName),
                Details = message.Details,
                Usage = ToUsage(message.Usage),
                AddedToolNames = message.AddedToolNames,
                Timestamp = message.Timestamp
            },
            "bashExecution" when message.Command is not null => new AgentBashExecutionMessage(
                message.Command,
                message.Output ?? string.Empty,
                message.ExitCode,
                message.Cancelled == true,
                message.Truncated == true,
                Normalize(message.FullOutputPath),
                message.Timestamp,
                message.ExcludeFromContext == true),
            "custom" when !string.IsNullOrWhiteSpace(message.CustomType) => new AgentCustomMessage(
                message.CustomType,
                content,
                message.Display == true,
                message.Details,
                message.Timestamp),
            "branchSummary" when message.Summary is not null && message.FromId is not null && message.Timestamp is { } branchTimestamp =>
                new AgentBranchSummaryMessage(message.Summary, message.FromId, branchTimestamp),
            "compactionSummary" when message.Summary is not null && message.TokensBefore is not null && message.Timestamp is { } compactionTimestamp =>
                new AgentCompactionSummaryMessage(message.Summary, message.TokensBefore.Value, compactionTimestamp),
            _ => null
        };
    }

    private static JsonElement SerializeMessageContent(IReadOnlyList<ContentBlock> content) =>
        JsonSerializer.SerializeToElement(
            content.Select(FromContent).ToArray(),
            AgentCoreSessionJsonContext.Default.SessionContentDtoArray);

    private static IReadOnlyList<ContentBlock> ToMessageContent(JsonElement? value)
    {
        if (value is null) return [];
        if (value.Value.ValueKind == JsonValueKind.String)
            return [new TextContent(value.Value.GetString() ?? string.Empty)];
        if (value.Value.ValueKind != JsonValueKind.Array) return [];

        return value.Value.EnumerateArray()
            .Select(item =>
            {
                SessionContentDto? dto;
                try { dto = JsonSerializer.Deserialize(item.GetRawText(), AgentCoreSessionJsonContext.Default.SessionContentDto); }
                catch (JsonException) { return null; }
                return dto is null ? null : ToContent(dto);
            })
            .Where(static item => item is not null)
            .Cast<ContentBlock>()
            .ToArray();
    }

    private static string? ToWireStopReason(StopReason? reason) => reason switch
    {
        StopReason.EndTurn => "stop",
        StopReason.MaxTokens => "length",
        StopReason.ToolUse => "toolUse",
        StopReason.ContentFilter => "error",
        StopReason.Error => "error",
        StopReason.Aborted => "aborted",
        StopReason.Deferred => "deferred",
        _ => null
    };

    private static StopReason? FromWireStopReason(string? reason) => reason switch
    {
        "stop" => StopReason.EndTurn,
        "length" => StopReason.MaxTokens,
        "toolUse" => StopReason.ToolUse,
        "error" => StopReason.Error,
        "aborted" => StopReason.Aborted,
        "deferred" => StopReason.Deferred,
        _ => null
    };

    private static SessionDeferredHandleDto? ToDeferredHandleDto(DeferredHandle? handle) =>
        handle is null
            ? null
            : new SessionDeferredHandleDto
            {
                Provider = handle.Provider,
                ModelId = handle.ModelId,
                Api = handle.Api,
                Id = handle.Id,
                ExpiresAt = handle.ExpiresAt,
                PollAfterMs = handle.PollAfterMs,
                Data = ToJsonElement(handle.Data)
            };

    private static DeferredHandle? ToDeferredHandle(SessionDeferredHandleDto? handle)
    {
        if (handle is null || string.IsNullOrWhiteSpace(handle.Provider) || string.IsNullOrWhiteSpace(handle.ModelId) ||
            string.IsNullOrWhiteSpace(handle.Api) || string.IsNullOrWhiteSpace(handle.Id)) return null;
        return new DeferredHandle(handle.Provider, handle.ModelId, handle.Api, handle.Id)
        {
            ExpiresAt = handle.ExpiresAt,
            PollAfterMs = handle.PollAfterMs,
            Data = handle.Data
        };
    }

    private static IReadOnlyList<AssistantMessageDiagnostic>? ParseDiagnostics(JsonElement? diagnostics)
    {
        if (diagnostics is null || diagnostics.Value.ValueKind != JsonValueKind.Array) return null;
        try { return JsonSerializer.Deserialize(diagnostics.Value.GetRawText(), AgentCoreSessionJsonContext.Default.AssistantMessageDiagnosticArray); }
        catch (JsonException) { return null; }
    }

    /// <summary>将 v4 compaction 的 retainedTail JSON 转为消息列表。</summary>
    /// <param name="value">retainedTail JSON 数组。</param>
    /// <param name="filePath">源文件路径。</param>
    /// <param name="lineNumber">行号。</param>
    /// <returns>保留尾部消息。</returns>
    private static IReadOnlyList<ChatMessage>? ToRetainedTail(JsonElement? value, string filePath, int lineNumber)
    {
        if (value is null) return null;
        var elementValue = value.Value;
        if (elementValue.ValueKind != JsonValueKind.Array) throw InvalidEntry(filePath, lineNumber, "has invalid retainedTail");
        var result = new List<ChatMessage>();
        foreach (var item in elementValue.EnumerateArray())
        {
            SessionMessageDto? dto;
            try { dto = JsonSerializer.Deserialize(item.GetRawText(), AgentCoreSessionJsonContext.Default.SessionMessageDto); }
            catch (JsonException ex) { throw InvalidEntry(filePath, lineNumber, "has invalid retainedTail message", ex); }
            var message = dto is null ? null : ToMessage(dto);
            if (message is null) throw InvalidEntry(filePath, lineNumber, "has invalid retainedTail message");
            result.Add(message);
        }
        return result;
    }

    private static SessionContentDto FromContent(ContentBlock content) =>
        content switch
        {
            TextContent text => new SessionContentDto
            {
                Type = "text",
                Text = text.Text,
                TextSignature = Normalize(text.TextSignature)
            },
            ThinkingContent thinking => new SessionContentDto
            {
                Type = "thinking",
                Thinking = thinking.Thinking,
                ThinkingSignature = Normalize(thinking.ThinkingSignature),
                Redacted = thinking.Redacted
            },
            ImageContent image => new SessionContentDto
            {
                Type = "image",
                Data = image.Data,
                MimeType = image.MimeType
            },
            ToolCallContent toolCall => new SessionContentDto
            {
                Type = "toolCall",
                Id = toolCall.Id,
                Name = toolCall.Name,
                Arguments = ParseToolCallArguments(toolCall.Arguments),
                ThoughtSignature = Normalize(toolCall.ThoughtSignature)
            },
            _ => new SessionContentDto { Type = content.Type }
        };

    /// <summary>
    /// 将 Tau 内部的工具参数字符串编码为 v4 toolCall.arguments JSON 值。
    /// </summary>
    /// <param name="arguments">工具调用内部保存的 JSON 文本。</param>
    /// <returns>合法 JSON 时返回解析后的值，否则返回旧字符串值以保留数据。</returns>
    private static JsonElement ParseToolCallArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return JsonSerializer.SerializeToElement("", AgentCoreSessionJsonContext.Default.String);

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(arguments, AgentCoreSessionJsonContext.Default.String);
        }
    }

    private static ContentBlock? ToContent(SessionContentDto content) =>
        content.Type switch
        {
            "text" when content.Text is not null => new TextContent(content.Text)
            {
                TextSignature = Normalize(content.TextSignature)
            },
            "thinking" when content.Thinking is not null => new ThinkingContent(content.Thinking)
            {
                ThinkingSignature = Normalize(content.ThinkingSignature),
                Redacted = content.Redacted
            },
            "image" when content.Data is not null && content.MimeType is not null => new ImageContent(
                content.Data,
                content.MimeType),
            "toolCall" when content.Id is not null && content.Name is not null && content.Arguments is not null =>
                new ToolCallContent(
                    content.Id,
                    content.Name,
                    content.Arguments.Value.ValueKind == JsonValueKind.String
                        ? content.Arguments.Value.GetString() ?? string.Empty
                        : content.Arguments.Value.GetRawText())
                {
                    ThoughtSignature = Normalize(content.ThoughtSignature)
                },
            _ => null
        };

    private static JsonElement FromCustomMessageContent(IReadOnlyList<ContentBlock> content) =>
        JsonSerializer.SerializeToElement(
            content.Select(FromContent).ToArray(),
            AgentCoreSessionJsonContext.Default.SessionContentDtoArray);

    private static IReadOnlyList<ContentBlock> ToCustomMessageContent(
        JsonElement content,
        string filePath,
        int lineNumber)
    {
        if (content.ValueKind == JsonValueKind.String)
            return [new TextContent(content.GetString() ?? string.Empty)];
        if (content.ValueKind != JsonValueKind.Array)
            throw InvalidEntry(filePath, lineNumber, "has invalid custom message content");

        var blocks = new List<ContentBlock>();
        foreach (var item in content.EnumerateArray())
        {
            SessionContentDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize(item.GetRawText(), AgentCoreSessionJsonContext.Default.SessionContentDto);
            }
            catch (JsonException ex)
            {
                throw InvalidEntry(filePath, lineNumber, "has invalid custom message content", ex);
            }

            var block = dto is null ? null : ToContent(dto);
            if (block is null)
                throw InvalidEntry(filePath, lineNumber, "has invalid custom message content");

            blocks.Add(block);
        }

        return blocks;
    }

    private static SessionUsageDto? FromUsage(Usage? usage) =>
        usage is null
            ? null
            : new SessionUsageDto
            {
                Input = usage.Value.InputTokens,
                Output = usage.Value.OutputTokens,
                CacheRead = usage.Value.CacheReadTokens,
                CacheWrite = usage.Value.CacheWriteTokens,
                CacheWrite1h = usage.Value.CacheWrite1hTokens,
                Reasoning = usage.Value.ReasoningTokens,
                // pi v4 要求 totalTokens；旧版 Usage 未填时按四个 token 维度推导
                TotalTokens = usage.Value.TotalTokens ??
                    usage.Value.InputTokens + usage.Value.OutputTokens +
                    (usage.Value.CacheReadTokens ?? 0) + (usage.Value.CacheWriteTokens ?? 0),
                ServiceTier = Normalize(usage.Value.ServiceTier),
                // pi v4 要求 cost 五个字段始终存在；内部没有计费信息时写入零值
                Cost = new SessionUsageCostDto
                {
                    Input = usage.Value.Cost?.Input ?? 0,
                    Output = usage.Value.Cost?.Output ?? 0,
                    CacheRead = usage.Value.Cost?.CacheRead ?? 0,
                    CacheWrite = usage.Value.Cost?.CacheWrite ?? 0,
                    Total = usage.Value.Cost is { } cost
                        ? cost.Input + cost.Output + cost.CacheRead + cost.CacheWrite
                        : 0
                }
            };

    private static Usage? ToUsage(SessionUsageDto? usage) =>
        usage is null
            ? null
            : new Usage(
                usage.Input ?? usage.InputTokens,
                usage.Output ?? usage.OutputTokens,
                usage.CacheRead ?? usage.CacheReadTokens,
                usage.CacheWrite ?? usage.CacheWriteTokens,
                Normalize(usage.ServiceTier),
                usage.Cost is null
                    ? null
                    : new UsageCost(
                        usage.Cost.Input,
                        usage.Cost.Output,
                        usage.Cost.CacheRead,
                        usage.Cost.CacheWrite))
            {
                CacheWrite1hTokens = usage.CacheWrite1h,
                ReasoningTokens = usage.Reasoning,
                TotalTokens = usage.TotalTokens
            };

    private static JsonElement? ToJsonElement(object? value)
    {
        var node = ToJsonNode(value);
        if (node is null) return null;
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.DeepClone();
            case JsonElement element:
                return JsonNode.Parse(element.GetRawText());
            case JsonDocument document:
                return JsonNode.Parse(document.RootElement.GetRawText());
            case AgentCompactionDetails details:
                return JsonSerializer.SerializeToNode(details, AgentCoreSessionJsonContext.Default.AgentCompactionDetails);
            case AgentBranchSummaryDetails details:
                return JsonSerializer.SerializeToNode(details, AgentCoreSessionJsonContext.Default.AgentBranchSummaryDetails);
            case AssistantMessageDiagnostic diagnostic:
                return JsonSerializer.SerializeToNode(diagnostic, AgentCoreSessionJsonContext.Default.AssistantMessageDiagnostic);
            case DiagnosticErrorInfo error:
                return JsonSerializer.SerializeToNode(error, AgentCoreSessionJsonContext.Default.DiagnosticErrorInfo);
            case string text:
                return JsonValue.Create(text);
            case bool boolean:
                return JsonValue.Create(boolean);
            case byte number:
                return JsonValue.Create(number);
            case sbyte number:
                return JsonValue.Create(number);
            case short number:
                return JsonValue.Create(number);
            case ushort number:
                return JsonValue.Create(number);
            case int number:
                return JsonValue.Create(number);
            case uint number:
                return JsonValue.Create(number);
            case long number:
                return JsonValue.Create(number);
            case ulong number:
                return JsonValue.Create(number);
            case float number:
                return JsonValue.Create(number);
            case double number:
                return JsonValue.Create(number);
            case decimal number:
                return JsonValue.Create(number);
            case DateTimeOffset timestamp:
                return JsonValue.Create(timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            case DateTime timestamp:
                return JsonValue.Create(timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            case IDictionary dictionary:
            {
                var result = new JsonObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is string key) result[key] = ToJsonNode(entry.Value);
                }

                return result;
            }
            case IEnumerable enumerable:
            {
                var result = new JsonArray();
                foreach (var item in enumerable) result.Add(ToJsonNode(item));
                return result;
            }
            default:
                {
                    // 未知扩展类型无法在 NativeAOT 下进行运行时反射序列化，保留稳定的文本表示
                    return JsonValue.Create(value.ToString());
                }
        }
    }

    private static SessionException InvalidSession(string filePath, string message, Exception? cause = null) =>
        new("invalid_session", $"Invalid JSONL session file {filePath}: {message}", cause);

    private static SessionException InvalidEntry(string filePath, int lineNumber, string message, Exception? cause = null) =>
        new("invalid_entry", $"Invalid JSONL session file {filePath}: line {lineNumber} {message}", cause);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(JsonlSessionHeader))]
[JsonSerializable(typeof(JsonlSessionEntryDto))]
[JsonSerializable(typeof(SessionMessageDto))]
[JsonSerializable(typeof(SessionMessageDto[]))]
[JsonSerializable(typeof(SessionContentDto))]
[JsonSerializable(typeof(SessionContentDto[]))]
[JsonSerializable(typeof(SessionUsageDto))]
[JsonSerializable(typeof(SessionUsageCostDto))]
[JsonSerializable(typeof(AssistantMessageDiagnostic[]))]
[JsonSerializable(typeof(AssistantMessageDiagnostic))]
[JsonSerializable(typeof(DiagnosticErrorInfo))]
[JsonSerializable(typeof(AgentCompactionDetails))]
[JsonSerializable(typeof(AgentBranchSummaryDetails))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string))]
internal sealed partial class AgentCoreSessionJsonContext : JsonSerializerContext;
