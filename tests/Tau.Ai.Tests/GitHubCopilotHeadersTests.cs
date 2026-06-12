using Tau.Ai.Providers;

namespace Tau.Ai.Tests;

// Direct unit coverage for the Tau-native port of upstream
// `packages/ai/src/providers/github-copilot-headers.ts`. Provider-level integration coverage (that
// the headers actually land on the request) lives in OpenAiResponsesProviderTests; these tests pin
// the helper's own contract: static header values, initiator inference and vision detection.
public sealed class GitHubCopilotHeadersTests
{
    private static ImageContent Image() => new("aGVsbG8=", "image/png");

    [Fact]
    public void CreateStaticHeaders_ReturnsExpectedClientIdentityHeaders()
    {
        var headers = GitHubCopilotHeaders.CreateStaticHeaders();

        Assert.Equal("GitHubCopilotChat/0.35.0", headers["User-Agent"]);
        Assert.Equal("vscode/1.107.0", headers["Editor-Version"]);
        Assert.Equal("copilot-chat/0.35.0", headers["Editor-Plugin-Version"]);
        Assert.Equal("vscode-chat", headers["Copilot-Integration-Id"]);
    }

    [Fact]
    public void CreateStaticHeaders_IsCaseInsensitive()
    {
        var headers = GitHubCopilotHeaders.CreateStaticHeaders();

        // Header lookups must be case-insensitive so they compose with HttpClient header handling.
        Assert.Equal("vscode-chat", headers["copilot-integration-id"]);
    }

    [Fact]
    public void InferInitiator_EmptyMessages_ReturnsUser()
    {
        Assert.Equal("user", GitHubCopilotHeaders.InferInitiator([]));
    }

    [Fact]
    public void InferInitiator_LastMessageUser_ReturnsUser()
    {
        IReadOnlyList<ChatMessage> messages =
        [
            new AssistantMessage([new TextContent("hi")]),
            new UserMessage("follow up")
        ];

        Assert.Equal("user", GitHubCopilotHeaders.InferInitiator(messages));
    }

    [Fact]
    public void InferInitiator_LastMessageAssistant_ReturnsAgent()
    {
        IReadOnlyList<ChatMessage> messages =
        [
            new UserMessage("question"),
            new AssistantMessage([new TextContent("answer")])
        ];

        Assert.Equal("agent", GitHubCopilotHeaders.InferInitiator(messages));
    }

    [Fact]
    public void InferInitiator_LastMessageToolResult_ReturnsAgent()
    {
        // A trailing tool result is an agent-initiated continuation, not a fresh user turn.
        IReadOnlyList<ChatMessage> messages =
        [
            new UserMessage("run it"),
            new ToolResultMessage("call_1", [new TextContent("done")])
        ];

        Assert.Equal("agent", GitHubCopilotHeaders.InferInitiator(messages));
    }

    [Fact]
    public void HasVisionInput_UserImage_ReturnsTrue()
    {
        IReadOnlyList<ChatMessage> messages = [new UserMessage([new TextContent("look"), Image()])];
        Assert.True(GitHubCopilotHeaders.HasVisionInput(messages));
    }

    [Fact]
    public void HasVisionInput_ToolResultImage_ReturnsTrue()
    {
        IReadOnlyList<ChatMessage> messages =
        [
            new ToolResultMessage("call_1", [new TextContent("screenshot"), Image()])
        ];

        Assert.True(GitHubCopilotHeaders.HasVisionInput(messages));
    }

    [Fact]
    public void HasVisionInput_NoImage_ReturnsFalse()
    {
        IReadOnlyList<ChatMessage> messages =
        [
            new UserMessage("text only"),
            new AssistantMessage([new TextContent("reply")])
        ];

        Assert.False(GitHubCopilotHeaders.HasVisionInput(messages));
    }

    [Fact]
    public void HasVisionInput_AssistantImage_ReturnsFalse()
    {
        // Upstream only inspects user and toolResult roles; an image on an assistant message must
        // not trigger the vision header.
        IReadOnlyList<ChatMessage> messages = [new AssistantMessage([new TextContent("here"), Image()])];
        Assert.False(GitHubCopilotHeaders.HasVisionInput(messages));
    }

    [Fact]
    public void BuildDynamicHeaders_WithoutVision_OmitsVisionHeader()
    {
        IReadOnlyList<ChatMessage> messages = [new UserMessage("hello")];

        var headers = GitHubCopilotHeaders.BuildDynamicHeaders(messages);

        Assert.Equal("user", headers["X-Initiator"]);
        Assert.Equal("conversation-edits", headers["Openai-Intent"]);
        Assert.False(headers.ContainsKey("Copilot-Vision-Request"));
    }

    [Fact]
    public void BuildDynamicHeaders_WithVision_SetsVisionHeaderAndAgentInitiator()
    {
        // Last message is a tool result carrying an image: agent-initiated and vision-bearing.
        IReadOnlyList<ChatMessage> messages =
        [
            new UserMessage("analyze"),
            new ToolResultMessage("call_1", [Image()])
        ];

        var headers = GitHubCopilotHeaders.BuildDynamicHeaders(messages);

        Assert.Equal("agent", headers["X-Initiator"]);
        Assert.Equal("conversation-edits", headers["Openai-Intent"]);
        Assert.Equal("true", headers["Copilot-Vision-Request"]);
    }
}
