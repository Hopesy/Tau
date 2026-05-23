using System.Globalization;
using System.Text.Json;
using Tau.Ai;

namespace Tau.Mom;

internal static class ChannelPromptDebugStore
{
    public const string PromptFileName = "last_prompt.jsonl";

    public static void Write(
        string workingDirectory,
        DelegationPromptParts promptParts,
        IReadOnlyList<ChatMessage> messages,
        DelegationRequest request,
        string provider,
        string model,
        string? sessionName,
        ILogger? logger = null)
    {
        try
        {
            var workingDirectoryFullPath = Path.GetFullPath(workingDirectory);
            Directory.CreateDirectory(workingDirectoryFullPath);
            var document = new ChannelPromptDebugContext(
                DateTimeOffset.UtcNow,
                provider,
                model,
                workingDirectoryFullPath,
                string.IsNullOrWhiteSpace(sessionName) ? null : sessionName.Trim(),
                promptParts.MomRuntimeContext,
                promptParts.DelegationContext,
                promptParts.RunnerInput,
                messages.Select(ToDebugMessage).ToArray(),
                request.Prompt,
                messages.Count,
                CountAttachments(request.Attachments),
                CountImageAttachments(workingDirectoryFullPath, request.Attachments));

            var json = JsonSerializer.Serialize(document, MomJsonContext.Default.ChannelPromptDebugContext);
            File.WriteAllText(Path.Combine(workingDirectoryFullPath, PromptFileName), MomSecretRedaction.RedactJson(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(ex, "Failed to write mom prompt debug context for {WorkingDirectory}.", workingDirectory);
        }
    }

    private static ChannelPromptDebugMessage ToDebugMessage(ChatMessage message)
    {
        return message switch
        {
            UserMessage user => new ChannelPromptDebugMessage(
                "user",
                user.Content.Select(ToDebugContent).ToArray()),
            AssistantMessage assistant => new ChannelPromptDebugMessage(
                "assistant",
                assistant.Content.Select(ToDebugContent).ToArray()),
            ToolResultMessage toolResult => new ChannelPromptDebugMessage(
                "toolResult",
                toolResult.Content.Select(ToDebugContent).ToArray(),
                toolResult.ToolCallId,
                toolResult.IsError),
            _ => new ChannelPromptDebugMessage(message.Role, [])
        };
    }

    private static ChannelPromptDebugContent ToDebugContent(ContentBlock content)
    {
        return content switch
        {
            TextContent text => new ChannelPromptDebugContent("text", Text: text.Text),
            ThinkingContent thinking => new ChannelPromptDebugContent("thinking", Text: thinking.Thinking),
            ImageContent image => new ChannelPromptDebugContent("image", MimeType: image.MimeType),
            ToolCallContent toolCall => new ChannelPromptDebugContent(
                "toolCall",
                ToolCallId: toolCall.Id,
                ToolName: toolCall.Name,
                Arguments: toolCall.Arguments),
            _ => new ChannelPromptDebugContent(content.Type)
        };
    }

    private static int CountAttachments(IReadOnlyList<string>? attachments)
    {
        return attachments?.Count(static attachment => !string.IsNullOrWhiteSpace(attachment)) ?? 0;
    }

    private static int CountImageAttachments(string workingDirectory, IReadOnlyList<string>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var attachment in attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment))
            {
                continue;
            }

            if (!TryResolvePath(attachment.Trim(), workingDirectory, out var fullPath) || !File.Exists(fullPath))
            {
                continue;
            }

            if (GetImageMimeType(fullPath) is not null)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryResolvePath(string value, string workingDirectory, out string fullPath)
    {
        try
        {
            fullPath = Path.IsPathRooted(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(value, workingDirectory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = value;
            return false;
        }
    }

    private static string? GetImageMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null
        };
    }
}

internal sealed record DelegationPromptParts(
    string MomRuntimeContext,
    string? DelegationContext,
    string RunnerInput);

internal sealed record ChannelPromptDebugContext(
    DateTimeOffset Date,
    string Provider,
    string Model,
    string WorkingDirectory,
    string? SessionName,
    string SystemPrompt,
    string? DelegationContext,
    string RunnerInput,
    IReadOnlyList<ChannelPromptDebugMessage> Messages,
    string NewUserMessage,
    int RestoredMessageCount,
    int AttachmentCount,
    int ImageAttachmentCount);

internal sealed record ChannelPromptDebugMessage(
    string Role,
    IReadOnlyList<ChannelPromptDebugContent> Content,
    string? ToolCallId = null,
    bool? IsError = null);

internal sealed record ChannelPromptDebugContent(
    string Type,
    string? Text = null,
    string? MimeType = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? Arguments = null);
