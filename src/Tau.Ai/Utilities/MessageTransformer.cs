namespace Tau.Ai.Utilities;

public static class MessageTransformer
{
    public const string NonVisionUserImagePlaceholder = "(image omitted: model does not support images)";
    public const string NonVisionToolImagePlaceholder = "(tool image omitted: model does not support images)";

    public static LlmContext DowngradeUnsupportedImages(LlmContext context, Model model)
    {
        var messages = DowngradeUnsupportedImages(context.Messages, model);
        return ReferenceEquals(messages, context.Messages)
            ? context
            : context with { Messages = messages };
    }

    public static IReadOnlyList<ChatMessage> DowngradeUnsupportedImages(
        IReadOnlyList<ChatMessage> messages,
        Model model)
    {
        if (SupportsImages(model))
        {
            return messages;
        }

        List<ChatMessage>? transformed = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var next = DowngradeMessage(message);
            if (!ReferenceEquals(next, message))
            {
                transformed ??= messages.Take(index).ToList();
            }

            transformed?.Add(next);
        }

        return transformed is null ? messages : transformed;
    }

    private static ChatMessage DowngradeMessage(ChatMessage message) =>
        message switch
        {
            UserMessage user => DowngradeUserMessage(user),
            ToolResultMessage toolResult => DowngradeToolResultMessage(toolResult),
            _ => message
        };

    private static ChatMessage DowngradeUserMessage(UserMessage message)
    {
        var content = ReplaceImagesWithPlaceholder(message.Content, NonVisionUserImagePlaceholder);
        return ReferenceEquals(content, message.Content)
            ? message
            : message with { Content = content };
    }

    private static ChatMessage DowngradeToolResultMessage(ToolResultMessage message)
    {
        var content = ReplaceImagesWithPlaceholder(message.Content, NonVisionToolImagePlaceholder);
        return ReferenceEquals(content, message.Content)
            ? message
            : message with { Content = content };
    }

    private static IReadOnlyList<ContentBlock> ReplaceImagesWithPlaceholder(
        IReadOnlyList<ContentBlock> content,
        string placeholder)
    {
        List<ContentBlock>? transformed = null;
        var previousWasPlaceholder = false;
        for (var index = 0; index < content.Count; index++)
        {
            var block = content[index];
            if (block is ImageContent)
            {
                transformed ??= content.Take(index).ToList();
                if (!previousWasPlaceholder)
                {
                    transformed.Add(new TextContent(placeholder));
                }

                previousWasPlaceholder = true;
                continue;
            }

            transformed?.Add(block);
            previousWasPlaceholder = block is TextContent text && text.Text == placeholder;
        }

        return transformed is null ? content : transformed;
    }

    private static bool SupportsImages(Model model) =>
        model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase);
}
