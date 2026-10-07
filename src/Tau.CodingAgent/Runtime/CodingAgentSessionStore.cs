using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentSessionSnapshot(
    IReadOnlyList<ChatMessage> Messages,
    string? Provider,
    string? Model,
    string? Name)
{
    /// <summary>保存的思考等级；null 表示旧会话未记录，off 表示明确关闭。</summary>
    public string? ThinkingLevel { get; init; }

    /// <summary>【CodingAgent】【模型恢复】压缩前仍保留的完整分支，仅用于运行时恢复选择。</summary>
    internal IReadOnlyList<CodingAgentTreeSessionEntry>? BranchEntries { get; init; }
}

public sealed class CodingAgentSessionStore
{
    private const int CurrentVersion = 1;
    private string _path;

    /// <summary>【CodingAgent】【快照迁移】让 SDK 的兼容快照随原生会话文件切换，避免新消息覆盖旧会话副本。</summary>
    /// <param name="treePath">当前原生 JSONL 路径。</param>
    internal void FollowTreePath(string treePath) => _path = System.IO.Path.ChangeExtension(treePath, ".json");

    public CodingAgentSessionStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? GetDefaultPath() : System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public static string GetDefaultPath()
    {
        var configured = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return System.IO.Path.GetFullPath(configured);
        }

        return System.IO.Path.Combine(Environment.CurrentDirectory, ".tau", "coding-agent-session.json");
    }

    public CodingAgentSessionSnapshot Load()
    {
        try
        {
            return LoadStrict();
        }
        catch (JsonException)
        {
            return new CodingAgentSessionSnapshot([], null, null, null);
        }
        catch (IOException)
        {
            return new CodingAgentSessionSnapshot([], null, null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new CodingAgentSessionSnapshot([], null, null, null);
        }
    }

    public CodingAgentSessionSnapshot LoadStrict()
    {
        if (!File.Exists(_path))
        {
            throw new IOException($"session file not found: {_path}");
        }

        using var stream = File.OpenRead(_path);
        var document = JsonSerializer.Deserialize(stream, CodingAgentSessionJsonContext.Default.CodingAgentSessionDocument);
        if (document is null || document.Version <= 0)
        {
            throw new JsonException("invalid coding agent session document");
        }

        var messages = document.Messages
            .Select(ToMessage)
            .Where(static message => message is not null)
            .Cast<ChatMessage>()
            .ToArray();

        var name = string.IsNullOrWhiteSpace(document.Name) ? null : document.Name.Trim();
        return new CodingAgentSessionSnapshot(messages, document.Provider, document.Model, name)
        {
            ThinkingLevel = document.ThinkingLevel
        };
    }

    public IReadOnlyList<ChatMessage> LoadMessages() => Load().Messages;

    /// <summary>【CodingAgent】【会话保存】保留原有保存入口，未提供思考等级时不写入该字段。</summary>
    /// <param name="messages">会话消息。</param>
    /// <param name="model">当前模型。</param>
    /// <param name="name">会话名称。</param>
    public void Save(IReadOnlyList<ChatMessage> messages, Model? model = null, string? name = null) =>
        Save(messages, model, name, null);

    /// <summary>【CodingAgent】【会话保存】保存完整会话及思考等级。</summary>
    /// <param name="messages">会话消息。</param>
    /// <param name="model">当前模型。</param>
    /// <param name="name">会话名称。</param>
    /// <param name="thinkingLevel">思考等级；null 保留未记录语义，off 为明确关闭。</param>
    public void Save(IReadOnlyList<ChatMessage> messages, Model? model, string? name, string? thinkingLevel)
    {
        var document = new CodingAgentSessionDocument
        {
            Version = CurrentVersion,
            Provider = model?.Provider,
            Model = model?.Id,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            ThinkingLevel = thinkingLevel,
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages = messages.Select(FromMessage).ToList()
        };

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, document, CodingAgentSessionJsonContext.Default.CodingAgentSessionDocument);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        File.Move(tempPath, _path);
    }

    /// <summary>【CodingAgent】【会话保存】转换消息并保留系统工具和段落增量。</summary>
    /// <param name="message">待保存消息。</param>
    /// <returns>平面会话 DTO。</returns>
    internal static CodingAgentSessionMessage FromMessage(ChatMessage message)
    {
        return message switch
        {
            SystemMessage system => new CodingAgentSessionMessage
            {
                Role = "system", Content = [FromContent(new TextContent(system.Content))],
                Sections = system.Sections, ToolsAdded = system.ToolsAdded, ToolsRemoved = system.ToolsRemoved, Timestamp = system.Timestamp
            },
            UserMessage user => new CodingAgentSessionMessage
            {
                Role = "user",
                Timestamp = user.Timestamp,
                Content = user.Content.Select(FromContent).ToList()
            },
            AssistantMessage assistant => new CodingAgentSessionMessage
            {
                Role = "assistant",
                Usage = FromUsage(assistant.Usage),
                Api = Normalize(assistant.Api),
                Provider = Normalize(assistant.Provider),
                Model = Normalize(assistant.Model),
                ProviderThinkingLevel = assistant.ProviderThinkingLevel,
                ThinkingLevel = assistant.ThinkingLevel,
                StopReason = assistant.StopReason,
                ErrorMessage = assistant.ErrorMessage,
                ResponseId = assistant.ResponseId,
                ResponseModel = assistant.ResponseModel,
                RawStopReason = assistant.RawStopReason,
                EndTurn = assistant.EndTurn,
                Diagnostics = assistant.Diagnostics,
                Deferred = assistant.Deferred is null ? null : assistant.Deferred with { Data = ToJsonElement(assistant.Deferred.Data) },
                Timestamp = assistant.Timestamp,
                Content = assistant.Content.Select(FromContent).ToList()
            },
            ToolResultMessage toolResult => new CodingAgentSessionMessage
            {
                Role = "toolResult",
                ToolCallId = toolResult.ToolCallId,
                IsError = toolResult.IsError,
                ToolName = toolResult.ToolName,
                Details = ToJsonElement(toolResult.Details),
                Usage = FromUsage(toolResult.Usage),
                NestedCalls = toolResult.NestedCalls,
                Timestamp = toolResult.Timestamp,
                AddedToolNames = toolResult.AddedToolNames,
                Content = toolResult.Content.Select(FromContent).ToList()
            },
            AgentCustomMessage custom => new CodingAgentSessionMessage
            {
                Role = "custom",
                CustomType = Normalize(custom.CustomType),
                Display = custom.Display,
                Details = ToJsonElement(custom.Details),
                Timestamp = custom.Timestamp,
                Content = custom.Content.Select(FromContent).ToList()
            },
            AgentBashExecutionMessage bash => new CodingAgentSessionMessage
            {
                Role = bash.Role, Command = bash.Command, Output = bash.Output, ExitCode = bash.ExitCode,
                Cancelled = bash.Cancelled, Truncated = bash.Truncated, FullOutputPath = bash.FullOutputPath,
                ExcludeFromContext = bash.ExcludeFromContext, Timestamp = bash.Timestamp
            },
            AgentBranchSummaryMessage branch => new CodingAgentSessionMessage
            {
                Role = branch.Role, Summary = branch.Summary, FromId = branch.FromId, Timestamp = branch.Timestamp
            },
            AgentCompactionSummaryMessage compaction => new CodingAgentSessionMessage
            {
                Role = compaction.Role, Summary = compaction.Summary, TokensBefore = compaction.TokensBefore, Timestamp = compaction.Timestamp
            },
            _ => new CodingAgentSessionMessage
            {
                Role = message.Role,
                Content = []
            }
        };
    }

    /// <summary>【CodingAgent】【会话恢复】从平面会话 DTO 恢复消息。</summary>
    /// <param name="message">已读取的 DTO。</param>
    /// <returns>支持的消息，未知角色返回 null。</returns>
    internal static ChatMessage? ToMessage(CodingAgentSessionMessage message)
    {
        var content = (message.Content ?? [])
            .Select(ToContent)
            .Where(static block => block is not null)
            .Cast<ContentBlock>()
            .ToArray();

        return message.Role switch
        {
            "system" => new SystemMessage(string.Join("\n", content.OfType<TextContent>().Select(text => text.Text)))
            {
                Sections = message.Sections, ToolsAdded = message.ToolsAdded, ToolsRemoved = message.ToolsRemoved,
                Timestamp = message.Timestamp ?? DateTimeOffset.UnixEpoch
            },
            "user" => new UserMessage(content) { Timestamp = message.Timestamp },
            "assistant" => new AssistantMessage(content)
            {
                Usage = ToUsage(message.Usage),
                Api = Normalize(message.Api),
                Provider = Normalize(message.Provider),
                Model = Normalize(message.Model),
                ProviderThinkingLevel = message.ProviderThinkingLevel,
                ThinkingLevel = message.ThinkingLevel,
                StopReason = message.StopReason,
                ErrorMessage = message.ErrorMessage,
                ResponseId = message.ResponseId,
                ResponseModel = message.ResponseModel,
                RawStopReason = message.RawStopReason,
                EndTurn = message.EndTurn,
                Diagnostics = message.Diagnostics,
                Deferred = message.Deferred,
                Timestamp = message.Timestamp
            },
            "toolResult" when !string.IsNullOrWhiteSpace(message.ToolCallId) => new ToolResultMessage(
                message.ToolCallId,
                content,
                message.IsError)
            {
                ToolName = message.ToolName, Details = message.Details?.Clone(), Usage = ToUsage(message.Usage), NestedCalls = message.NestedCalls,
                AddedToolNames = message.AddedToolNames, Timestamp = message.Timestamp
            },
            "custom" when !string.IsNullOrWhiteSpace(message.CustomType) => new AgentCustomMessage(
                message.CustomType,
                content,
                message.Display,
                message.Details?.Clone(),
                message.Timestamp),
            "bashExecution" => new AgentBashExecutionMessage(message.Command ?? "", message.Output ?? "", message.ExitCode,
                message.Cancelled, message.Truncated, message.FullOutputPath, message.Timestamp, message.ExcludeFromContext),
            "branchSummary" => new AgentBranchSummaryMessage(message.Summary ?? "", message.FromId ?? "", message.Timestamp ?? DateTimeOffset.UnixEpoch),
            "compactionSummary" => new AgentCompactionSummaryMessage(message.Summary ?? "", message.TokensBefore ?? 0, message.Timestamp ?? DateTimeOffset.UnixEpoch),
            _ => null
        };
    }

    private static CodingAgentSessionUsage? FromUsage(Usage? usage)
    {
        if (usage is not { } value)
        {
            return null;
        }

        return new CodingAgentSessionUsage
        {
            InputTokens = value.InputTokens,
            OutputTokens = value.OutputTokens,
            CacheReadTokens = value.CacheReadTokens,
            CacheWriteTokens = value.CacheWriteTokens,
            CacheWrite1hTokens = value.CacheWrite1hTokens,
            ReasoningTokens = value.ReasoningTokens,
            TotalTokens = value.TotalTokens,
            ServiceTier = Normalize(value.ServiceTier),
            Cost = FromUsageCost(value.Cost)
        };
    }

    private static Usage? ToUsage(CodingAgentSessionUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new Usage(
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheReadTokens,
            usage.CacheWriteTokens,
            Normalize(usage.ServiceTier),
            ToUsageCost(usage.Cost))
        {
            CacheWrite1hTokens = usage.CacheWrite1hTokens, ReasoningTokens = usage.ReasoningTokens, TotalTokens = usage.TotalTokens
        };
    }

    private static CodingAgentSessionUsageCost? FromUsageCost(UsageCost? cost)
    {
        if (cost is not { } value)
        {
            return null;
        }

        return new CodingAgentSessionUsageCost
        {
            Input = value.Input,
            Output = value.Output,
            CacheRead = value.CacheRead,
            CacheWrite = value.CacheWrite,
            Total = value.Total
        };
    }

    private static UsageCost? ToUsageCost(CodingAgentSessionUsageCost? cost)
    {
        if (cost is null)
        {
            return null;
        }

        return new UsageCost(cost.Input, cost.Output, cost.CacheRead, cost.CacheWrite);
    }

    private static CodingAgentSessionContent FromContent(ContentBlock block)
    {
        return block switch
        {
            TextContent text => new CodingAgentSessionContent
            {
                Type = "text",
                Text = text.Text,
                TextSignature = text.TextSignature
            },
            ThinkingContent thinking => new CodingAgentSessionContent
            {
                Type = "thinking",
                Thinking = thinking.Thinking,
                ThinkingSignature = thinking.ThinkingSignature,
                Redacted = thinking.Redacted
            },
            ImageContent image => new CodingAgentSessionContent
            {
                Type = "image",
                Data = image.Data,
                MimeType = image.MimeType
            },
            ToolCallContent toolCall => new CodingAgentSessionContent
            {
                Type = "toolCall",
                Id = toolCall.Id,
                Name = toolCall.Name,
                Arguments = toolCall.Arguments,
                ThoughtSignature = toolCall.ThoughtSignature,
                Namespace = toolCall.Namespace
            },
            _ => new CodingAgentSessionContent { Type = block.Type }
        };
    }

    /// <summary>【CodingAgent】【内容恢复】转换已保存的内容块及协议签名。</summary>
    /// <param name="content">内容 DTO。</param>
    /// <returns>支持的内容块，未知类型返回空值。</returns>
    internal static ContentBlock? ToContent(CodingAgentSessionContent content)
    {
        return content.Type switch
        {
            "text" when content.Text is not null => new TextContent(content.Text) { TextSignature = content.TextSignature },
            "thinking" when content.Thinking is not null => new ThinkingContent(content.Thinking) { ThinkingSignature = content.ThinkingSignature, Redacted = content.Redacted },
            "image" when content.Data is not null && content.MimeType is not null => new ImageContent(content.Data, content.MimeType),
            "toolCall" when content.Id is not null && content.Name is not null && content.Arguments is not null => new ToolCallContent(
                content.Id,
                content.Name,
                content.Arguments) { ThoughtSignature = content.ThoughtSignature, Namespace = content.Namespace },
            _ => null
        };
    }

    /// <summary>【CodingAgent】【结构化详情】复制 JSON 数据或序列化调用方对象，避免退化为类型名称。</summary>
    /// <param name="value">可序列化的详情对象。</param>
    /// <returns>独立的 JSON 数据。</returns>
    private static JsonElement? ToJsonElement(object? value)
    {
        return value switch
        {
            null => null,
            JsonElement element => element.Clone(),
            JsonDocument document => document.RootElement.Clone(),
            _ => JsonSerializer.SerializeToElement(value, value.GetType())
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class CodingAgentSessionDocument
{
    public string? ThinkingLevel { get; init; }
    public int Version { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Name { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public List<CodingAgentSessionMessage> Messages { get; init; } = [];
}

internal sealed class CodingAgentSessionMessage
{
    public StopReason? StopReason { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ResponseId { get; init; }
    public string? ResponseModel { get; init; }
    public string? RawStopReason { get; init; }
    public bool? EndTurn { get; init; }
    public IReadOnlyList<AssistantMessageDiagnostic>? Diagnostics { get; init; }
    public DeferredHandle? Deferred { get; init; }
    public string? ToolName { get; init; }
    public IReadOnlyList<string>? AddedToolNames { get; init; }
    public string? Command { get; init; }
    public string? Output { get; init; }
    public int? ExitCode { get; init; }
    public bool Cancelled { get; init; }
    public bool Truncated { get; init; }
    public bool ExcludeFromContext { get; init; }
    public string? FullOutputPath { get; init; }
    public string? Summary { get; init; }
    public string? FromId { get; init; }
    public int? TokensBefore { get; init; }
    /// <summary>恢复 Anthropic 历史 effort 所需的原生等级。</summary>
    public string? ProviderThinkingLevel { get; init; }
    /// <summary>【CodingAgent】【路由记录】本次物理请求的通用推理等级。</summary>
    public string? ThinkingLevel { get; init; }
    public IReadOnlyDictionary<string, string?>? Sections { get; init; }
    public IReadOnlyList<Tool>? ToolsAdded { get; init; }
    public IReadOnlyList<ToolReference>? ToolsRemoved { get; init; }
    public string Role { get; init; } = string.Empty;
    public string? ToolCallId { get; init; }
    public bool IsError { get; init; }
    public CodingAgentSessionUsage? Usage { get; init; }
    public NestedToolCalls? NestedCalls { get; init; }
    public string? Api { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? CustomType { get; init; }
    public bool Display { get; init; }
    public JsonElement? Details { get; init; }
    public List<CodingAgentSessionContent> Content { get; init; } = [];
}

internal sealed class CodingAgentSessionUsage
{
    public int? CacheWrite1hTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int? TotalTokens { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int? CacheReadTokens { get; init; }
    public int? CacheWriteTokens { get; init; }
    public string? ServiceTier { get; init; }
    public CodingAgentSessionUsageCost? Cost { get; init; }
}

internal sealed class CodingAgentSessionUsageCost
{
    public decimal Input { get; init; }
    public decimal Output { get; init; }
    public decimal CacheRead { get; init; }
    public decimal CacheWrite { get; init; }
    public decimal Total { get; init; }
}

internal sealed class CodingAgentSessionContent
{
    public string? TextSignature { get; init; }
    public string? ThinkingSignature { get; init; }
    public bool Redacted { get; init; }
    public string? ThoughtSignature { get; init; }
    /// <summary>【CodingAgent】【工具命名空间】持久化动态工具的来源空间。</summary>
    public string? Namespace { get; init; }
    public string Type { get; init; } = string.Empty;
    public string? Text { get; init; }
    public string? Thinking { get; init; }
    public string? Data { get; init; }
    public string? MimeType { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Arguments { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters = [typeof(CodingAgentSessionMessageConverter)],
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CodingAgentSessionDocument))]
[JsonSerializable(typeof(string))]
internal sealed partial class CodingAgentSessionJsonContext : JsonSerializerContext;
