namespace Tau.Ai.Providers;

public static class GitHubCopilotHeaders
{
    public static Dictionary<string, string> CreateStaticHeaders() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "GitHubCopilotChat/0.35.0",
            ["Editor-Version"] = "vscode/1.107.0",
            ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
            ["Copilot-Integration-Id"] = "vscode-chat"
        };

    public static Dictionary<string, string> BuildDynamicHeaders(IReadOnlyList<ChatMessage> messages)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Initiator"] = InferInitiator(messages),
            ["Openai-Intent"] = "conversation-edits"
        };

        if (HasVisionInput(messages))
        {
            headers["Copilot-Vision-Request"] = "true";
        }

        return headers;
    }

    public static string InferInitiator(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return "user";
        }

        return string.Equals(messages[^1].Role, "user", StringComparison.OrdinalIgnoreCase)
            ? "user"
            : "agent";
    }

    public static bool HasVisionInput(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user when user.Content.Any(block => block is ImageContent):
                case ToolResultMessage toolResult when toolResult.Content.Any(block => block is ImageContent):
                    return true;
            }
        }

        return false;
    }
}
