using System.Text.Json;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

[CollectionDefinition(nameof(CodingAgentTreeSessionRedactionCollection), DisableParallelization = true)]
public sealed class CodingAgentTreeSessionRedactionCollection;

[Collection(nameof(CodingAgentTreeSessionRedactionCollection))]
public sealed class CodingAgentTreeSessionRedactionTests
{
    private const string OpenAiKey = "sk-EXAMPLE_BASE_KEY_99999999";
    private const string GitHubToken = "ghp_AAAA1111BBBB2222CCCC3333DDDD4444EEEE";
    private const string AwsKey = "AKIAIOSFODNN7EXAMPLE";
    private const string BearerToken = "Bearer abcdef1234567890abcdef1234567890";

    [Fact]
    public void WritesJsonlSessionEntries_RedactsStringValuesByDefault()
    {
        using var environment = TemporaryEnvironmentVariable.Set(
            TauSecretRedactor.CodingAgentEnvironmentVariable,
            null);
        var path = CreateTempPath("tau-tree-redaction-default");

        try
        {
            var store = new CodingAgentTreeSessionStore(path);
            store.AppendSessionInfo($"session {OpenAiKey}", $"provider {BearerToken}", $"model {AwsKey}");
            store.AppendMessages(
                [
                    new UserMessage($"use {OpenAiKey}"),
                    new AssistantMessage(
                        [
                            new ToolCallContent(
                                "call-1",
                                "shell",
                                $$"""{"token":"{{GitHubToken}}","enabled":true,"count":7,"value":null}""")
                        ])
                ],
                0);
            store.AppendCompaction($"summary {AwsKey}", firstKeptEntryId: null, tokensBefore: 42, fromHook: true);

            var jsonl = File.ReadAllText(path);
            Assert.DoesNotContain(OpenAiKey, jsonl, StringComparison.Ordinal);
            Assert.DoesNotContain(GitHubToken, jsonl, StringComparison.Ordinal);
            Assert.DoesNotContain(AwsKey, jsonl, StringComparison.Ordinal);
            Assert.DoesNotContain(BearerToken, jsonl, StringComparison.Ordinal);
            Assert.Contains(TauSecretRedactor.Placeholder, jsonl, StringComparison.Ordinal);

            var lines = File.ReadAllLines(path);
            using var sessionInfo = JsonDocument.Parse(FindLine(lines, "\"type\":\"session_info\""));
            Assert.Equal($"session {TauSecretRedactor.Placeholder}", sessionInfo.RootElement.GetProperty("name").GetString());
            Assert.Equal($"provider {TauSecretRedactor.Placeholder}", sessionInfo.RootElement.GetProperty("provider").GetString());
            Assert.Equal($"model {TauSecretRedactor.Placeholder}", sessionInfo.RootElement.GetProperty("model").GetString());

            using var userMessage = JsonDocument.Parse(FindLine(lines, "\"role\":\"user\""));
            Assert.Equal(
                $"use {TauSecretRedactor.Placeholder}",
                userMessage.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());

            using var toolMessage = JsonDocument.Parse(FindLine(lines, "\"arguments\""));
            Assert.Contains(
                TauSecretRedactor.Placeholder,
                toolMessage.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("arguments").GetString(),
                StringComparison.Ordinal);

            using var compaction = JsonDocument.Parse(FindLine(lines, "\"type\":\"compaction\""));
            Assert.Equal($"summary {TauSecretRedactor.Placeholder}", compaction.RootElement.GetProperty("summary").GetString());
            Assert.Equal(42, compaction.RootElement.GetProperty("tokensBefore").GetInt32());
            Assert.True(compaction.RootElement.GetProperty("fromHook").GetBoolean());
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void WritesJsonlSessionEntries_OptOutEnvironmentKeepsOriginalStringValues()
    {
        using var environment = TemporaryEnvironmentVariable.Set(
            TauSecretRedactor.CodingAgentEnvironmentVariable,
            "0");
        var path = CreateTempPath("tau-tree-redaction-optout");

        try
        {
            var store = new CodingAgentTreeSessionStore(path);
            store.AppendSessionInfo($"session {OpenAiKey}");
            store.AppendMessages([new UserMessage($"use {GitHubToken}")], 0);

            var jsonl = File.ReadAllText(path);
            Assert.Contains(OpenAiKey, jsonl, StringComparison.Ordinal);
            Assert.Contains(GitHubToken, jsonl, StringComparison.Ordinal);
            Assert.DoesNotContain(TauSecretRedactor.Placeholder, jsonl, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void WritesJsonlSessionEntries_PreservesObjectKeys()
    {
        var path = CreateTempPath("tau-tree-redaction-keys");

        try
        {
            var store = new CodingAgentTreeSessionStore(path, secretRedactor: new TauSecretRedactor(enabled: true));
            store.AppendMessages([new UserMessage($"value {OpenAiKey}")], 0);

            using var message = JsonDocument.Parse(FindLine(File.ReadAllLines(path), "\"type\":\"message\""));
            var rootKeys = message.RootElement.EnumerateObject().Select(static property => property.Name).ToArray();
            Assert.Contains("type", rootKeys);
            Assert.Contains("id", rootKeys);
            Assert.Contains("timestamp", rootKeys);
            Assert.Contains("message", rootKeys);
            Assert.DoesNotContain(TauSecretRedactor.Placeholder, rootKeys);

            var messageKeys = message.RootElement.GetProperty("message").EnumerateObject().Select(static property => property.Name).ToArray();
            Assert.Contains("role", messageKeys);
            Assert.Contains("content", messageKeys);

            var contentKeys = message.RootElement
                .GetProperty("message")
                .GetProperty("content")[0]
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray();
            Assert.Contains("type", contentKeys);
            Assert.Contains("text", contentKeys);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void WritesJsonlSessionEntries_PersistsAssistantUsageCost()
    {
        var path = CreateTempPath("tau-tree-usage-cost");
        var timestamp = new DateTimeOffset(2026, 6, 9, 12, 34, 56, TimeSpan.Zero);

        try
        {
            var store = new CodingAgentTreeSessionStore(path, secretRedactor: new TauSecretRedactor(enabled: true));
            store.AppendMessages(
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
                ],
                0);

            var line = FindLine(File.ReadAllLines(path), "\"role\":\"assistant\"");
            using var entry = JsonDocument.Parse(line);
            var message = entry.RootElement.GetProperty("message");
            Assert.Equal("openai", message.GetProperty("provider").GetString());
            Assert.Equal("gpt-5.4", message.GetProperty("model").GetString());
            Assert.Equal("openai-responses", message.GetProperty("api").GetString());
            Assert.Equal(timestamp, message.GetProperty("timestamp").GetDateTimeOffset());
            var usage = message.GetProperty("usage");
            Assert.Equal(1_000_000, usage.GetProperty("inputTokens").GetInt32());
            Assert.Equal(2_000_000, usage.GetProperty("outputTokens").GetInt32());
            Assert.Equal(300_000, usage.GetProperty("cacheReadTokens").GetInt32());
            Assert.Equal(400_000, usage.GetProperty("cacheWriteTokens").GetInt32());
            Assert.Equal("priority", usage.GetProperty("serviceTier").GetString());
            Assert.Equal(37.1m, usage.GetProperty("cost").GetProperty("total").GetDecimal());

            var snapshot = store.LoadCurrentBranchSnapshot();
            var assistant = Assert.IsType<AssistantMessage>(Assert.Single(snapshot.Messages));
            Assert.Equal("openai", assistant.Provider);
            Assert.Equal("gpt-5.4", assistant.Model);
            Assert.Equal(37.1m, assistant.Usage!.Value.Cost!.Value.Total);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void GetCurrentBranchUsageSummary_SumsAssistantUsageCostAcrossCompactionBranch()
    {
        var path = CreateTempPath("tau-tree-usage-summary");

        try
        {
            var store = new CodingAgentTreeSessionStore(path, secretRedactor: new TauSecretRedactor(enabled: true));
            store.AppendMessages(
                [
                    new UserMessage("first"),
                    new AssistantMessage([new TextContent("priced one")])
                    {
                        Usage = new Usage(
                            InputTokens: 100,
                            OutputTokens: 20,
                            CacheReadTokens: 3,
                            CacheWriteTokens: 4,
                            ServiceTier: "flex",
                            Cost: new UsageCost(0.01m, 0.02m, 0.003m, 0.004m))
                    },
                    new UserMessage("second"),
                    new AssistantMessage([new TextContent("priced two")])
                    {
                        Usage = new Usage(
                            InputTokens: 50,
                            OutputTokens: 5,
                            CacheReadTokens: 1,
                            CacheWriteTokens: 2,
                            ServiceTier: "default",
                            Cost: new UsageCost(0.005m, 0.006m, 0.001m, 0.002m))
                    }
                ],
                0);
            store.AppendCompaction("summary keeps branch history", firstKeptEntryId: null, tokensBefore: 90);

            var summary = store.GetCurrentBranchUsageSummary();

            Assert.Equal(150, summary.Tokens.Input);
            Assert.Equal(25, summary.Tokens.Output);
            Assert.Equal(4, summary.Tokens.CacheRead);
            Assert.Equal(6, summary.Tokens.CacheWrite);
            Assert.Equal(185, summary.Tokens.Total);
            Assert.Equal(0.051m, summary.Cost);
            Assert.Equal(2, summary.CostRecords);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void LoadCurrentBranchSnapshot_RestoresTreeAfterRedactedWrites()
    {
        var path = CreateTempPath("tau-tree-redaction-restore");

        try
        {
            var store = new CodingAgentTreeSessionStore(path, secretRedactor: new TauSecretRedactor(enabled: true));
            var ids = store.AppendMessages(
                [
                    new UserMessage($"root {OpenAiKey}"),
                    new AssistantMessage([new TextContent("old branch")])
                ],
                0);
            store.Branch(ids[0]);
            store.AppendMessages([new UserMessage($"after {GitHubToken}")], 0);

            var snapshot = store.LoadCurrentBranchSnapshot();

            Assert.Equal(2, snapshot.Messages.Count);
            Assert.Equal($"root {TauSecretRedactor.Placeholder}", SingleText(Assert.IsType<UserMessage>(snapshot.Messages[0])));
            Assert.Equal($"after {TauSecretRedactor.Placeholder}", SingleText(Assert.IsType<UserMessage>(snapshot.Messages[1])));
            Assert.DoesNotContain(snapshot.Messages, message => SingleTextOrEmpty(message) == "old branch");
            Assert.NotNull(snapshot.LeafId);
            Assert.Equal(4, snapshot.EntryCount);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    private static string CreateTempPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.jsonl");

    private static string FindLine(IEnumerable<string> lines, string needle) =>
        lines.Single(line => line.Contains(needle, StringComparison.Ordinal));

    private static string SingleText(ChatMessage message)
    {
        var text = SingleTextOrEmpty(message);
        Assert.False(string.IsNullOrEmpty(text));
        return text;
    }

    private static string SingleTextOrEmpty(ChatMessage message)
    {
        var content = message switch
        {
            UserMessage user => user.Content,
            AssistantMessage assistant => assistant.Content,
            ToolResultMessage toolResult => toolResult.Content,
            _ => []
        };

        return content.OfType<TextContent>().SingleOrDefault()?.Text ?? string.Empty;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class TemporaryEnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        private TemporaryEnvironmentVariable(string name, string? value)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public static TemporaryEnvironmentVariable Set(string name, string? value) => new(name, value);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
        }
    }
}
