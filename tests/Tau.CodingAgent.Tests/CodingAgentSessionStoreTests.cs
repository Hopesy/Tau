using System.Text.Json;
using Tau.AgentCore.Harness;
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
    public void SaveAndLoad_RoundTripsCustomMessage()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-session-custom-{Guid.NewGuid():N}.json");
        var store = new CodingAgentSessionStore(path);
        var timestamp = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

        try
        {
            using var detailsDocument = JsonDocument.Parse("""{"level":"error","attempt":2}""");

            store.Save(
                [
                    new AgentCustomMessage(
                        "status-update",
                        [new TextContent("deployed")],
                        true,
                        detailsDocument.RootElement.Clone(),
                        timestamp)
                ]);

            var loaded = store.Load();

            var custom = Assert.IsType<AgentCustomMessage>(Assert.Single(loaded.Messages));
            Assert.Equal("status-update", custom.CustomType);
            Assert.True(custom.Display);
            Assert.Equal(timestamp, custom.Timestamp);
            Assert.Equal("deployed", Assert.IsType<TextContent>(Assert.Single(custom.Content)).Text);
            var details = Assert.IsType<JsonElement>(custom.Details);
            Assert.Equal("error", details.GetProperty("level").GetString());
            Assert.Equal(2, details.GetProperty("attempt").GetInt32());

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var message = document.RootElement.GetProperty("messages")[0];
            Assert.Equal("custom", message.GetProperty("role").GetString());
            Assert.Equal("status-update", message.GetProperty("customType").GetString());
            Assert.True(message.GetProperty("display").GetBoolean());
            Assert.Equal("error", message.GetProperty("details").GetProperty("level").GetString());
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
    public void TreeSessionStore_RoundTripsCustomMessageThroughMessageEntries()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-tree-custom-{Guid.NewGuid():N}.jsonl");
        var store = new CodingAgentTreeSessionStore(path);

        try
        {
            using var detailsDocument = JsonDocument.Parse("""{"source":"extension"}""");
            store.AppendMessages(
                [
                    new AgentCustomMessage(
                        "plan-state",
                        new ContentBlock[] { new TextContent("persisted") },
                        false,
                        detailsDocument.RootElement.Clone())
                ],
                startIndex: 0);

            var loaded = store.LoadCurrentBranchSnapshot();

            var custom = Assert.IsType<AgentCustomMessage>(Assert.Single(loaded.Messages));
            Assert.Equal("plan-state", custom.CustomType);
            Assert.False(custom.Display);
            Assert.Equal("persisted", Assert.IsType<TextContent>(Assert.Single(custom.Content)).Text);
            var details = Assert.IsType<JsonElement>(custom.Details);
            Assert.Equal("extension", details.GetProperty("source").GetString());
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
    public void SaveAndLoad_RoundTripsAssistantUsageCostAndMetadata()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-coding-agent-session-usage-{Guid.NewGuid():N}.json");
        var store = new CodingAgentSessionStore(path);
        var timestamp = new DateTimeOffset(2026, 6, 9, 12, 34, 56, TimeSpan.Zero);

        try
        {
            store.Save(
                [
                    new AssistantMessage([new TextContent("priced")])
                    {
                        Provider = "openai",
                        Model = "gpt-5.4",
                        Api = "openai-responses",
                        Timestamp = timestamp,
                        Usage = new Usage(
                            InputTokens: 1_000_000,
                            OutputTokens: 2_000_000,
                            CacheReadTokens: 300_000,
                            CacheWriteTokens: 400_000,
                            ServiceTier: "priority",
                            Cost: new UsageCost(4m, 32m, 0.3m, 0.8m))
                    }
                ]);

            var loaded = store.Load();

            var assistant = Assert.IsType<AssistantMessage>(Assert.Single(loaded.Messages));
            Assert.Equal("openai", assistant.Provider);
            Assert.Equal("gpt-5.4", assistant.Model);
            Assert.Equal("openai-responses", assistant.Api);
            Assert.Equal(timestamp, assistant.Timestamp);
            Assert.NotNull(assistant.Usage);
            Assert.Equal(1_000_000, assistant.Usage!.Value.InputTokens);
            Assert.Equal(2_000_000, assistant.Usage.Value.OutputTokens);
            Assert.Equal(300_000, assistant.Usage.Value.CacheReadTokens);
            Assert.Equal(400_000, assistant.Usage.Value.CacheWriteTokens);
            Assert.Equal("priority", assistant.Usage.Value.ServiceTier);
            Assert.NotNull(assistant.Usage.Value.Cost);
            Assert.Equal(37.1m, assistant.Usage.Value.Cost!.Value.Total);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var message = document.RootElement.GetProperty("messages")[0];
            Assert.Equal("openai", message.GetProperty("provider").GetString());
            Assert.Equal("gpt-5.4", message.GetProperty("model").GetString());
            Assert.Equal("openai-responses", message.GetProperty("api").GetString());
            Assert.Equal(37.1m, message.GetProperty("usage").GetProperty("cost").GetProperty("total").GetDecimal());
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
