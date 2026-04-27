using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.WebUi.Contracts;
using Tau.WebUi.Services;

namespace Tau.CodingAgent.Tests;

public class RuntimeCodingAgentRunnerTests
{
    [Fact]
    public void Create_WithExplicitProviderAndModel_UsesRequestedModel()
    {
        var runner = RuntimeCodingAgentRunner.Create("google", "gemini-2.5-pro");

        Assert.Equal("google", runner.Model.Provider, ignoreCase: true);
        Assert.Equal("gemini-2.5-pro", runner.Model.Id, ignoreCase: true);
        Assert.Empty(runner.Messages);
    }

    [Fact]
    public void Create_WithInitialMessages_RehydratesConversationState()
    {
        var runner = RuntimeCodingAgentRunner.Create(
            "openai",
            "gpt-5.4",
            [new UserMessage("hello"), new AssistantMessage([new TextContent("world")])]);

        Assert.Equal(2, runner.Messages.Count);
        Assert.IsType<UserMessage>(runner.Messages[0]);
        Assert.IsType<AssistantMessage>(runner.Messages[1]);
    }
}

public class WebChatStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSessions()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-webui-{Guid.NewGuid():N}.json");
        var store = new WebChatStore(path);
        var sessions = new[]
        {
            new WebChatSessionDto(
                "session-1",
                "Test Session",
                "openai",
                "gpt-5.4",
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow,
                true,
                [new WebChatMessageDto("user", "hello", DateTimeOffset.UtcNow)])
        };

        try
        {
            store.Save(sessions);
            var loaded = store.Load();

            var session = Assert.Single(loaded);
            Assert.Equal("session-1", session.Id);
            Assert.Equal("openai", session.Provider);
            Assert.Equal("gpt-5.4", session.Model);
            Assert.Single(session.Messages);
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
