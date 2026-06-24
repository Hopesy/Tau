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
    string? ParentSession = null);

internal sealed record JsonlSessionEntryDto
{
    public string? Type { get; init; }
    public string? Id { get; init; }
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

    public static string SerializeEntry(SessionTreeEntry entry) =>
        JsonSerializer.Serialize(ToDto(entry), AgentCoreSessionJsonContext.Default.JsonlSessionEntryDto);

    public static JsonlSessionHeader ParseHeader(string line, string filePath)
    {
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
            header.Version != CurrentVersion ||
            string.IsNullOrWhiteSpace(header.Id) ||
            string.IsNullOrWhiteSpace(header.Timestamp) ||
            string.IsNullOrWhiteSpace(header.Cwd))
        {
            throw InvalidSession(filePath, "first line is not a valid session header");
        }

        return header;
    }

    public static SessionTreeEntry ParseEntry(string line, string filePath, int lineNumber)
    {
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

        return dto.Type switch
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
    }

    private static JsonlSessionEntryDto ToDto(SessionTreeEntry entry) =>
        entry switch
        {
            MessageSessionEntry message => new JsonlSessionEntryDto
            {
                Type = message.Type,
                Id = message.Id,
                ParentId = message.ParentId,
                Timestamp = message.Timestamp,
                Message = FromMessage(message.Message)
            },
            ThinkingLevelChangeSessionEntry thinking => new JsonlSessionEntryDto
            {
                Type = thinking.Type,
                Id = thinking.Id,
                ParentId = thinking.ParentId,
                Timestamp = thinking.Timestamp,
                ThinkingLevel = thinking.ThinkingLevel
            },
            ModelChangeSessionEntry model => new JsonlSessionEntryDto
            {
                Type = model.Type,
                Id = model.Id,
                ParentId = model.ParentId,
                Timestamp = model.Timestamp,
                Provider = model.Provider,
                ModelId = model.ModelId
            },
            ActiveToolsChangeSessionEntry tools => new JsonlSessionEntryDto
            {
                Type = tools.Type,
                Id = tools.Id,
                ParentId = tools.ParentId,
                Timestamp = tools.Timestamp,
                ActiveToolNames = tools.ActiveToolNames.ToArray()
            },
            CompactionSessionEntry compaction => new JsonlSessionEntryDto
            {
                Type = compaction.Type,
                Id = compaction.Id,
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
                ParentId = custom.ParentId,
                Timestamp = custom.Timestamp,
                CustomType = custom.CustomType,
                Data = ToJsonElement(custom.Data)
            },
            CustomMessageSessionEntry customMessage => new JsonlSessionEntryDto
            {
                Type = customMessage.Type,
                Id = customMessage.Id,
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
                ParentId = label.ParentId,
                Timestamp = label.Timestamp,
                TargetId = label.TargetId,
                Label = label.Label
            },
            SessionInfoEntry info => new JsonlSessionEntryDto
            {
                Type = info.Type,
                Id = info.Id,
                ParentId = info.ParentId,
                Timestamp = info.Timestamp,
                Name = info.Name
            },
            LeafSessionEntry leaf => new JsonlSessionEntryDto
            {
                Type = leaf.Type,
                Id = leaf.Id,
                ParentId = leaf.ParentId,
                Timestamp = leaf.Timestamp,
                TargetId = leaf.TargetId
            },
            BranchSummarySessionEntry summary => new JsonlSessionEntryDto
            {
                Type = summary.Type,
                Id = summary.Id,
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
[JsonSerializable(typeof(string))]
internal sealed partial class AgentCoreSessionJsonContext : JsonSerializerContext;
