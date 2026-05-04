using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentSessionStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsConversationContent()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-session-{Guid.NewGuid():N}.json");
        var store = new CodingAgentSessionStore(path);
        var model = new Model
        {
            Provider = "google",
            Id = "gemini-2.5-pro",
            Name = "Gemini 2.5 Pro",
            Api = "google-gemini"
        };

        try
        {
            store.Save(
                [
                    new UserMessage([new TextContent("hello"), new ImageContent("abc", "image/png")]),
                    new AssistantMessage([new ThinkingContent("reason"), new TextContent("world"), new ToolCallContent("call-1", "read_file", "{\"path\":\"README.md\"}")]),
                    new ToolResultMessage("call-1", [new TextContent("done")], IsError: true)
                ],
                model,
                "Focused session");

            var loaded = store.Load();

            Assert.Equal("google", loaded.Provider);
            Assert.Equal("gemini-2.5-pro", loaded.Model);
            Assert.Equal("Focused session", loaded.Name);
            Assert.Equal(3, loaded.Messages.Count);

            var user = Assert.IsType<UserMessage>(loaded.Messages[0]);
            Assert.Equal("hello", Assert.IsType<TextContent>(user.Content[0]).Text);
            var image = Assert.IsType<ImageContent>(user.Content[1]);
            Assert.Equal("abc", image.Data);
            Assert.Equal("image/png", image.MimeType);

            var assistant = Assert.IsType<AssistantMessage>(loaded.Messages[1]);
            Assert.Equal("reason", Assert.IsType<ThinkingContent>(assistant.Content[0]).Thinking);
            Assert.Equal("world", Assert.IsType<TextContent>(assistant.Content[1]).Text);
            var toolCall = Assert.IsType<ToolCallContent>(assistant.Content[2]);
            Assert.Equal("call-1", toolCall.Id);
            Assert.Equal("read_file", toolCall.Name);

            var toolResult = Assert.IsType<ToolResultMessage>(loaded.Messages[2]);
            Assert.Equal("call-1", toolResult.ToolCallId);
            Assert.True(toolResult.IsError);
            Assert.Equal("done", Assert.IsType<TextContent>(Assert.Single(toolResult.Content)).Text);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Load_InvalidJson_ReturnsEmptySnapshot()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-session-invalid-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, "not json");

            var loaded = new CodingAgentSessionStore(path).Load();

            Assert.Empty(loaded.Messages);
            Assert.Null(loaded.Provider);
            Assert.Null(loaded.Model);
            Assert.Null(loaded.Name);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void LoadStrict_MissingFile_Throws()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-session-missing-{Guid.NewGuid():N}.json");

        var ex = Assert.Throws<IOException>(() => new CodingAgentSessionStore(path).LoadStrict());
        Assert.Equal($"session file not found: {System.IO.Path.GetFullPath(path)}", ex.Message);
    }

    [Fact]
    public void LoadStrict_InvalidJson_Throws()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-session-invalid-strict-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, "not json");

            Assert.Throws<System.Text.Json.JsonException>(() => new CodingAgentSessionStore(path).LoadStrict());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
