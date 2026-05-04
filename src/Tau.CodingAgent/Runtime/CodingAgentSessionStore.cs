using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentSessionSnapshot(
    IReadOnlyList<ChatMessage> Messages,
    string? Provider,
    string? Model,
    string? Name);

public sealed class CodingAgentSessionStore
{
    private const int CurrentVersion = 1;
    private readonly string _path;

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
        return new CodingAgentSessionSnapshot(messages, document.Provider, document.Model, name);
    }

    public IReadOnlyList<ChatMessage> LoadMessages() => Load().Messages;

    public void Save(IReadOnlyList<ChatMessage> messages, Model? model = null, string? name = null)
    {
        var document = new CodingAgentSessionDocument
        {
            Version = CurrentVersion,
            Provider = model?.Provider,
            Model = model?.Id,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
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

    private static CodingAgentSessionMessage FromMessage(ChatMessage message)
    {
        return message switch
        {
            UserMessage user => new CodingAgentSessionMessage
            {
                Role = "user",
                Content = user.Content.Select(FromContent).ToList()
            },
            AssistantMessage assistant => new CodingAgentSessionMessage
            {
                Role = "assistant",
                Content = assistant.Content.Select(FromContent).ToList()
            },
            ToolResultMessage toolResult => new CodingAgentSessionMessage
            {
                Role = "toolResult",
                ToolCallId = toolResult.ToolCallId,
                IsError = toolResult.IsError,
                Content = toolResult.Content.Select(FromContent).ToList()
            },
            _ => new CodingAgentSessionMessage
            {
                Role = message.Role,
                Content = []
            }
        };
    }

    private static ChatMessage? ToMessage(CodingAgentSessionMessage message)
    {
        var content = message.Content
            .Select(ToContent)
            .Where(static block => block is not null)
            .Cast<ContentBlock>()
            .ToArray();

        return message.Role switch
        {
            "user" => new UserMessage(content),
            "assistant" => new AssistantMessage(content),
            "toolResult" when !string.IsNullOrWhiteSpace(message.ToolCallId) => new ToolResultMessage(
                message.ToolCallId,
                content,
                message.IsError),
            _ => null
        };
    }

    private static CodingAgentSessionContent FromContent(ContentBlock block)
    {
        return block switch
        {
            TextContent text => new CodingAgentSessionContent
            {
                Type = "text",
                Text = text.Text
            },
            ThinkingContent thinking => new CodingAgentSessionContent
            {
                Type = "thinking",
                Thinking = thinking.Thinking
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
                Arguments = toolCall.Arguments
            },
            _ => new CodingAgentSessionContent { Type = block.Type }
        };
    }

    private static ContentBlock? ToContent(CodingAgentSessionContent content)
    {
        return content.Type switch
        {
            "text" when content.Text is not null => new TextContent(content.Text),
            "thinking" when content.Thinking is not null => new ThinkingContent(content.Thinking),
            "image" when content.Data is not null && content.MimeType is not null => new ImageContent(content.Data, content.MimeType),
            "toolCall" when content.Id is not null && content.Name is not null && content.Arguments is not null => new ToolCallContent(
                content.Id,
                content.Name,
                content.Arguments),
            _ => null
        };
    }
}

internal sealed class CodingAgentSessionDocument
{
    public int Version { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Name { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public List<CodingAgentSessionMessage> Messages { get; init; } = [];
}

internal sealed class CodingAgentSessionMessage
{
    public string Role { get; init; } = string.Empty;
    public string? ToolCallId { get; init; }
    public bool IsError { get; init; }
    public List<CodingAgentSessionContent> Content { get; init; } = [];
}

internal sealed class CodingAgentSessionContent
{
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
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CodingAgentSessionDocument))]
internal sealed partial class CodingAgentSessionJsonContext : JsonSerializerContext;
