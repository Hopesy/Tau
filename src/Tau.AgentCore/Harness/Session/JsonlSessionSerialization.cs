using System.Text.Json;
using System.Text.Json.Serialization;
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
    public string? ThinkingLevel { get; init; }
    public string? Provider { get; init; }
    public string? ModelId { get; init; }
    public IReadOnlyList<string>? ActiveToolNames { get; init; }
    public string? FirstKeptEntryId { get; init; }
    public int? TokensBefore { get; init; }
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

internal sealed record SessionMessageDto
{
    public string? Role { get; init; }
    public IReadOnlyList<SessionContentDto>? Content { get; init; }
    public SessionUsageDto? Usage { get; init; }
    public string? Api { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ResponseId { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool IsError { get; init; }
}

internal sealed record SessionUsageDto
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int? CacheReadTokens { get; init; }
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
    public string? Arguments { get; init; }
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
        if (parentSessionId is not null && legacyParentSessionPath is not null)
            throw new ArgumentException("v4 header cannot contain both parent session id and legacy path.");

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

    /// <summary>序列化 v4 lane 指针 mutation。</summary>
    /// <param name="sequence">共享序号。</param>
    /// <param name="lane">lane 名称。</param>
    /// <param name="leafId">目标叶 entry id。</param>
    /// <returns>单行 JSON。</returns>
    public static string SerializeLaneMutation(long sequence, string lane, string? leafId) =>
        SerializeJsonLine(writer =>
        {
            writer.WriteString("kind", "lane");
            writer.WriteNumber("seq", sequence);
            writer.WriteString("lane", lane);
            if (leafId is null)
                writer.WriteNull("leafId");
            else
                writer.WriteString("leafId", leafId);
        });

    /// <summary>序列化 v4 全局 fact mutation。</summary>
    /// <param name="sequence">共享序号。</param>
    /// <param name="fact">fact 类型，name 或 label。</param>
    /// <param name="name">session 名称。</param>
    /// <param name="targetId">label 目标 entry id。</param>
    /// <param name="label">label 文本。</param>
    /// <returns>单行 JSON。</returns>
    public static string SerializeFactMutation(long sequence, string fact, string? name = null, string? targetId = null, string? label = null) =>
        SerializeJsonLine(writer =>
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
                var version = root.TryGetProperty("version", out var versionValue) ? versionValue.GetInt32() : 0;
                if (version != 4 ||
                    !root.TryGetProperty("id", out var idValue) ||
                    idValue.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("createdAt", out var createdAtValue) ||
                    createdAtValue.ValueKind != JsonValueKind.Number ||
                    !createdAtValue.TryGetInt64(out var createdAt) ||
                    createdAt < 0 ||
                    !root.TryGetProperty("cwd", out var cwdValue) ||
                    cwdValue.ValueKind != JsonValueKind.String)
                {
                    throw InvalidSession(filePath, "first line is not a valid v4 session header");
                }

                var parentSessionId = root.TryGetProperty("parentSessionId", out var parentValue) &&
                                      parentValue.ValueKind == JsonValueKind.String
                    ? parentValue.GetString()
                    : null;
                var legacyParent = root.TryGetProperty("legacyParentSessionPath", out var legacyValue) &&
                                   legacyValue.ValueKind == JsonValueKind.String
                    ? legacyValue.GetString()
                    : null;
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
                    Metadata = root.TryGetProperty("metadata", out var metadataValue) && metadataValue.ValueKind == JsonValueKind.Object
                        ? metadataValue.Clone()
                        : null
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

    /// <summary>
    /// 将 lane record 编码为 JSONL v4 mutation 行。
    /// </summary>
    /// <param name="record">待持久化的 lane record。</param>
    /// <returns>包含 kind、序号和 record 字段的单行 JSON。</returns>
    public static string SerializeRecord(LaneRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
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
                fields["operationKind"] = started.OperationKind;
                fields["sourceLeafId"] = started.SourceLeafId;
                break;
            case OperationFinishedRecord finished:
                fields["runId"] = finished.RunId;
                fields["outcome"] = finished.Outcome;
                break;
            case AbortRequestedRecord abort:
                fields["runId"] = abort.RunId;
                break;
            case StepAttemptRecord attempt:
                fields["runId"] = attempt.RunId;
                fields["step"] = attempt.Step;
                fields["attempt"] = attempt.Attempt;
                fields["resultEntryId"] = attempt.ResultEntryId;
                fields["compactionReason"] = attempt.CompactionReason;
                break;
            case ToolStartedRecord tool:
                fields["runId"] = tool.RunId;
                fields["assistantEntryId"] = tool.AssistantEntryId;
                fields["toolIndex"] = tool.ToolIndex;
                fields["toolCallId"] = tool.ToolCallId;
                fields["toolName"] = tool.ToolName;
                fields["resultEntryId"] = tool.ResultEntryId;
                break;
            case QueueEnqueuedRecord queued:
                fields["queue"] = queued.Queue;
                fields["entryId"] = queued.EntryId;
                fields["runId"] = queued.RunId;
                break;
            case QueueCancelledRecord cancelled:
                fields["entryId"] = cancelled.EntryId;
                fields["runId"] = cancelled.RunId;
                break;
            case WriteDeferredRecord deferred:
                fields["runId"] = deferred.RunId;
                fields["entryId"] = deferred.EntryId;
                break;
            case UsageRecord usage:
                fields["cause"] = usage.Cause;
                fields["runId"] = usage.RunId;
                fields["entryId"] = usage.EntryId;
                fields["attempt"] = usage.Attempt;
                if (usage.Usage is { } usageValue)
                {
                    fields["usage"] = new Dictionary<string, object?>
                    {
                        ["inputTokens"] = usageValue.InputTokens,
                        ["outputTokens"] = usageValue.OutputTokens,
                        ["cacheReadTokens"] = usageValue.CacheReadTokens,
                        ["cacheWriteTokens"] = usageValue.CacheWriteTokens,
                        ["reasoningTokens"] = usageValue.ReasoningTokens,
                        ["totalTokens"] = usageValue.TotalTokens
                    };
                }
                break;
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
            string? OptionalString(string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
            int OptionalInt(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
            Usage? ParseUsage()
            {
                if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
                    return null;
                var input = usageElement.TryGetProperty("inputTokens", out var inputElement) && inputElement.TryGetInt32(out var inputValue) ? inputValue : 0;
                var output = usageElement.TryGetProperty("outputTokens", out var outputElement) && outputElement.TryGetInt32(out var outputValue) ? outputValue : 0;
                var cacheRead = usageElement.TryGetProperty("cacheReadTokens", out var cacheReadElement) && cacheReadElement.TryGetInt32(out var cacheReadValue) ? cacheReadValue : (int?)null;
                var cacheWrite = usageElement.TryGetProperty("cacheWriteTokens", out var cacheWriteElement) && cacheWriteElement.TryGetInt32(out var cacheWriteValue) ? cacheWriteValue : (int?)null;
                var reasoning = usageElement.TryGetProperty("reasoningTokens", out var reasoningElement) && reasoningElement.TryGetInt32(out var reasoningValue) ? reasoningValue : (int?)null;
                var total = usageElement.TryGetProperty("totalTokens", out var totalElement) && totalElement.TryGetInt32(out var totalValue) ? totalValue : (int?)null;
                return new Usage(input, output, cacheRead, cacheWrite) { ReasoningTokens = reasoning, TotalTokens = total };
            }

            return type switch
            {
                "operation_started" => new OperationStartedRecord(id, sequence, lane, timestamp, RequiredString(root, "operationKind", filePath, lineNumber), OptionalString("sourceLeafId")),
                "operation_finished" => new OperationFinishedRecord(id, sequence, lane, timestamp, RequiredString(root, "runId", filePath, lineNumber), RequiredString(root, "outcome", filePath, lineNumber)),
                "abort_requested" => new AbortRequestedRecord(id, sequence, lane, timestamp, RequiredString(root, "runId", filePath, lineNumber)),
                "step_attempt" => new StepAttemptRecord(id, sequence, lane, timestamp, RequiredString(root, "runId", filePath, lineNumber), RequiredString(root, "step", filePath, lineNumber), OptionalInt("attempt"), RequiredString(root, "resultEntryId", filePath, lineNumber), OptionalString("compactionReason")),
                "tool_started" => new ToolStartedRecord(id, sequence, lane, timestamp, RequiredString(root, "runId", filePath, lineNumber), RequiredString(root, "assistantEntryId", filePath, lineNumber), OptionalInt("toolIndex"), RequiredString(root, "toolCallId", filePath, lineNumber), RequiredString(root, "toolName", filePath, lineNumber), RequiredString(root, "resultEntryId", filePath, lineNumber)),
                "queue_enqueued" => new QueueEnqueuedRecord(id, sequence, lane, timestamp, RequiredString(root, "queue", filePath, lineNumber), RequiredString(root, "entryId", filePath, lineNumber), OptionalString("runId")),
                "queue_cancelled" => new QueueCancelledRecord(id, sequence, lane, timestamp, RequiredString(root, "entryId", filePath, lineNumber), OptionalString("runId")),
                "write_deferred" => new WriteDeferredRecord(id, sequence, lane, timestamp, RequiredString(root, "runId", filePath, lineNumber), RequiredString(root, "entryId", filePath, lineNumber)),
                "usage" => new UsageRecord(id, sequence, lane, timestamp, RequiredString(root, "cause", filePath, lineNumber), OptionalString("runId"), OptionalString("entryId"), OptionalInt("attempt"), ParseUsage()),
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
                ToMessage(dto.Message) ?? throw InvalidEntry(filePath, lineNumber, "has invalid message")),
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
            "compaction" when dto.Summary is not null &&
                dto.FirstKeptEntryId is not null &&
                dto.TokensBefore is not null => new CompactionSessionEntry(
                dto.Id,
                dto.ParentId,
                dto.Timestamp,
                dto.Summary,
                dto.FirstKeptEntryId,
                dto.TokensBefore.Value,
                dto.Details,
                dto.FromHook == true),
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
                dto.FromHook == true),
            _ => throw InvalidEntry(filePath, lineNumber, "is not a supported session entry")
        };
        return (dto.Seq is > 0 ? parsed with { Sequence = dto.Seq.Value } : parsed) with { Lane = dto.Lane };
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
                Message = FromMessage(message.Message)
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
                FromHook = compaction.FromHook
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
                FromHook = summary.FromHook
            },
            _ => throw new InvalidOperationException($"Unsupported session entry type: {entry.Type}")
        };

    private static SessionMessageDto FromMessage(ChatMessage message) =>
        message switch
        {
            UserMessage user => new SessionMessageDto
            {
                Role = "user",
                Content = user.Content.Select(FromContent).ToArray()
            },
            AssistantMessage assistant => new SessionMessageDto
            {
                Role = "assistant",
                Content = assistant.Content.Select(FromContent).ToArray(),
                Usage = FromUsage(assistant.Usage),
                Api = Normalize(assistant.Api),
                Provider = Normalize(assistant.Provider),
                Model = Normalize(assistant.Model),
                ResponseId = Normalize(assistant.ResponseId),
                Timestamp = assistant.Timestamp
            },
            ToolResultMessage toolResult => new SessionMessageDto
            {
                Role = "toolResult",
                Content = toolResult.Content.Select(FromContent).ToArray(),
                ToolCallId = toolResult.ToolCallId,
                ToolName = Normalize(toolResult.ToolName),
                IsError = toolResult.IsError
            },
            _ => new SessionMessageDto
            {
                Role = message.Role,
                Content = []
            }
        };

    private static ChatMessage? ToMessage(SessionMessageDto message)
    {
        var content = message.Content?
            .Select(ToContent)
            .Where(static block => block is not null)
            .Cast<ContentBlock>()
            .ToArray() ?? [];

        return message.Role switch
        {
            "user" => new UserMessage(content),
            "assistant" => new AssistantMessage(content)
            {
                Usage = ToUsage(message.Usage),
                Api = Normalize(message.Api),
                Provider = Normalize(message.Provider),
                Model = Normalize(message.Model),
                ResponseId = Normalize(message.ResponseId),
                Timestamp = message.Timestamp
            },
            "toolResult" when !string.IsNullOrWhiteSpace(message.ToolCallId) => new ToolResultMessage(
                message.ToolCallId,
                content,
                message.IsError)
            {
                ToolName = Normalize(message.ToolName)
            },
            _ => null
        };
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
                Arguments = toolCall.Arguments,
                ThoughtSignature = Normalize(toolCall.ThoughtSignature)
            },
            _ => new SessionContentDto { Type = content.Type }
        };

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
                new ToolCallContent(content.Id, content.Name, content.Arguments)
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
                InputTokens = usage.Value.InputTokens,
                OutputTokens = usage.Value.OutputTokens,
                CacheReadTokens = usage.Value.CacheReadTokens,
                CacheWriteTokens = usage.Value.CacheWriteTokens,
                ServiceTier = Normalize(usage.Value.ServiceTier),
                Cost = usage.Value.Cost is null
                    ? null
                    : new SessionUsageCostDto
                    {
                        Input = usage.Value.Cost.Value.Input,
                        Output = usage.Value.Cost.Value.Output,
                        CacheRead = usage.Value.Cost.Value.CacheRead,
                        CacheWrite = usage.Value.Cost.Value.CacheWrite
                    }
            };

    private static Usage? ToUsage(SessionUsageDto? usage) =>
        usage is null
            ? null
            : new Usage(
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheReadTokens,
                usage.CacheWriteTokens,
                Normalize(usage.ServiceTier),
                usage.Cost is null
                    ? null
                    : new UsageCost(
                        usage.Cost.Input,
                        usage.Cost.Output,
                        usage.Cost.CacheRead,
                        usage.Cost.CacheWrite));

    private static JsonElement? ToJsonElement(object? value)
    {
        return value switch
        {
            null => null,
            JsonElement element => element.Clone(),
            JsonDocument document => document.RootElement.Clone(),
            AgentCompactionDetails details => JsonSerializer.SerializeToElement(
                details,
                AgentCoreSessionJsonContext.Default.AgentCompactionDetails),
            AgentBranchSummaryDetails details => JsonSerializer.SerializeToElement(
                details,
                AgentCoreSessionJsonContext.Default.AgentBranchSummaryDetails),
            _ => JsonSerializer.SerializeToElement(value.ToString(), AgentCoreSessionJsonContext.Default.String)
        };
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
[JsonSerializable(typeof(SessionContentDto))]
[JsonSerializable(typeof(SessionContentDto[]))]
[JsonSerializable(typeof(SessionUsageDto))]
[JsonSerializable(typeof(SessionUsageCostDto))]
[JsonSerializable(typeof(AgentCompactionDetails))]
[JsonSerializable(typeof(AgentBranchSummaryDetails))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string))]
internal sealed partial class AgentCoreSessionJsonContext : JsonSerializerContext;
