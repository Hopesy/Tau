using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Tests;

public sealed class AgentCompactionSummariesTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
        "2026-01-01T00:00:00.000Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void BuildSummaryPrompt_IncludesConversationPreviousSummaryAndAdditionalFocus()
    {
        var prompt = AgentCompactionSummaries.BuildSummaryPrompt(
            [new UserMessage("new work")],
            customInstructions: "keep blockers",
            previousSummary: "old summary");

        Assert.Contains("<conversation>", prompt, StringComparison.Ordinal);
        Assert.Contains("[User]: new work", prompt, StringComparison.Ordinal);
        Assert.Contains("<previous-summary>\nold summary\n</previous-summary>", prompt, StringComparison.Ordinal);
        Assert.Contains(AgentCompactionSummaries.UpdateSummarizationPrompt, prompt, StringComparison.Ordinal);
        Assert.Contains("Additional focus: keep blockers", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentCompactionSummaries.SummarizationPrompt, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateSummaryAsync_SendsPromptOptionsAndReturnsText()
    {
        var provider = new RecordingSummaryProvider(static (_, _) =>
            new AssistantMessage([new TextContent("summary text")])
            {
                StopReason = StopReason.EndTurn
            });
        var model = CreateModel(provider.Api) with
        {
            Reasoning = true,
            MaxOutputTokens = 512
        };

        var summary = await AgentCompactionSummaries.GenerateSummaryAsync(
            [new UserMessage("hello")],
            CreateOptions(provider, model) with
            {
                CustomInstructions = "focus tests",
                ReserveTokens = 1_000,
                ThinkingLevel = ThinkingLevel.High
            });

        Assert.Equal("summary text", summary);
        var call = Assert.Single(provider.Calls);
        Assert.Equal(AgentCompactionSummaries.SummarizationSystemPrompt, call.SystemPrompt);
        Assert.Contains("[User]: hello", call.Prompt, StringComparison.Ordinal);
        Assert.Contains("Additional focus: focus tests", call.Prompt, StringComparison.Ordinal);
        Assert.Equal(512, call.MaxTokens);
        Assert.Equal(ThinkingLevel.High, call.Reasoning);
    }

    [Fact]
    public async Task GenerateSummaryAsync_MapsAbortedAndErrorResponses()
    {
        var abortedProvider = new RecordingSummaryProvider(static (_, _) =>
            new AssistantMessage
            {
                StopReason = StopReason.Aborted,
                ErrorMessage = "cancelled"
            });
        var aborted = await Assert.ThrowsAsync<AgentSummaryException>(() =>
            AgentCompactionSummaries.GenerateSummaryAsync(
                [new UserMessage("hello")],
                CreateOptions(abortedProvider)));
        Assert.Equal("aborted", aborted.Code);
        Assert.Equal("cancelled", aborted.Message);

        var errorProvider = new RecordingSummaryProvider(static (_, _) =>
            new AssistantMessage
            {
                StopReason = StopReason.Error,
                ErrorMessage = "provider failed"
            });
        var failed = await Assert.ThrowsAsync<AgentSummaryException>(() =>
            AgentCompactionSummaries.GenerateSummaryAsync(
                [new UserMessage("hello")],
                CreateOptions(errorProvider)));
        Assert.Equal("summarization_failed", failed.Code);
        Assert.Equal("Summarization failed: provider failed", failed.Message);
    }

    [Fact]
    public async Task CompactAsync_GeneratesUpdatedSummaryAndAppendsFileMetadata()
    {
        var provider = new RecordingSummaryProvider(static (_, _) =>
            new AssistantMessage([new TextContent("updated summary")])
            {
                StopReason = StopReason.EndTurn
            });
        var fileOperations = AgentCompaction.CreateFileOperations();
        fileOperations.Read.Add("README.md");
        fileOperations.Edited.Add("src/Edit.cs");
        var preparation = new AgentCompactionPreparation(
            "kept-entry",
            [new UserMessage("new work")],
            [],
            IsSplitTurn: false,
            TokensBefore: 123,
            PreviousSummary: "old summary",
            fileOperations,
            new AgentCompactionSettings(ReserveTokens: 1_000));

        var result = await AgentCompactionSummaries.CompactAsync(preparation, CreateOptions(provider));

        Assert.Equal("kept-entry", result.FirstKeptEntryId);
        Assert.Equal(123, result.TokensBefore);
        Assert.Contains("updated summary", result.Summary, StringComparison.Ordinal);
        Assert.Contains("<previous-summary>\nold summary\n</previous-summary>", provider.Calls.Single().Prompt, StringComparison.Ordinal);
        Assert.Equal(["README.md"], result.Details.ReadFiles);
        Assert.Equal(["src/Edit.cs"], result.Details.ModifiedFiles);
        Assert.Contains("<read-files>\nREADME.md\n</read-files>", result.Summary, StringComparison.Ordinal);
        Assert.Contains("<modified-files>\nsrc/Edit.cs\n</modified-files>", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactAsync_GeneratesSplitTurnPrefixSummary()
    {
        var provider = new RecordingSummaryProvider(static (context, _) =>
        {
            var prompt = ReadPrompt(context);
            var text = prompt.Contains(AgentCompactionSummaries.TurnPrefixSummarizationPrompt, StringComparison.Ordinal)
                ? "turn prefix summary"
                : "history summary";
            return new AssistantMessage([new TextContent(text)])
            {
                StopReason = StopReason.EndTurn
            };
        });
        var preparation = new AgentCompactionPreparation(
            "kept-entry",
            [new UserMessage("old history")],
            [new UserMessage("turn prefix")],
            IsSplitTurn: true,
            TokensBefore: 250,
            PreviousSummary: null,
            AgentCompaction.CreateFileOperations(),
            AgentCompactionSettings.Default);

        var result = await AgentCompactionSummaries.CompactAsync(preparation, CreateOptions(provider));

        Assert.Contains("history summary", result.Summary, StringComparison.Ordinal);
        Assert.Contains("**Turn Context (split turn):**", result.Summary, StringComparison.Ordinal);
        Assert.Contains("turn prefix summary", result.Summary, StringComparison.Ordinal);
        Assert.Equal(2, provider.Calls.Count);
    }

    [Fact]
    public async Task GenerateBranchSummaryAsync_GeneratesSummaryAndDetails()
    {
        var provider = new RecordingSummaryProvider(static (_, _) =>
            new AssistantMessage([new TextContent("branch body")])
            {
                StopReason = StopReason.EndTurn
            });
        var entries = new SessionTreeEntry[]
        {
            Entry("user-1", new UserMessage("branch request")),
            Entry("assistant-1", new AssistantMessage(
            [
                new ToolCallContent("read-1", "read_file", """{"path":"README.md"}"""),
                new ToolCallContent("write-1", "write", """{"path":"src/New.cs"}""")
            ]))
        };

        var result = await AgentBranchSummaries.GenerateBranchSummaryAsync(
            entries,
            CreateOptions(provider) with
            {
                CustomInstructions = "only blockers",
                ReplaceInstructions = true,
                ReserveTokens = 1_000
            });

        Assert.StartsWith(AgentBranchSummaries.BranchSummaryPreamble, result.Summary, StringComparison.Ordinal);
        Assert.Contains("branch body", result.Summary, StringComparison.Ordinal);
        Assert.Contains("<read-files>\nREADME.md\n</read-files>", result.Summary, StringComparison.Ordinal);
        Assert.Contains("<modified-files>\nsrc/New.cs\n</modified-files>", result.Summary, StringComparison.Ordinal);
        Assert.Equal(["README.md"], result.ReadFiles);
        Assert.Equal(["src/New.cs"], result.ModifiedFiles);
        var call = Assert.Single(provider.Calls);
        Assert.Contains("[User]: branch request", call.Prompt, StringComparison.Ordinal);
        Assert.Contains("only blockers", call.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentBranchSummaries.BranchSummaryPrompt, call.Prompt, StringComparison.Ordinal);
        Assert.Equal(2_048, call.MaxTokens);
    }

    [Fact]
    public async Task GenerateBranchSummaryAsync_ReturnsNoContentWithoutProviderCall()
    {
        var provider = new RecordingSummaryProvider(static (_, _) =>
            new AssistantMessage([new TextContent("should not run")]));

        var result = await AgentBranchSummaries.GenerateBranchSummaryAsync(
            [],
            CreateOptions(provider));

        Assert.Equal("No content to summarize", result.Summary);
        Assert.Empty(result.ReadFiles);
        Assert.Empty(result.ModifiedFiles);
        Assert.Empty(provider.Calls);
    }

    /// <summary>摘要被截断或调用工具时拒绝生成可保存的检查点。</summary>
    /// <param name="toolCall">是否返回工具调用。</param>
    /// <returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerateSummary_RejectsIncompleteOrToolResponses(bool toolCall)
    {
        var provider = new RecordingSummaryProvider((_, _) => new AssistantMessage(toolCall
            ? [new ToolCallContent("call", "read", "{}")] : [new TextContent("partial")])
            { StopReason = toolCall ? StopReason.ToolUse : StopReason.MaxTokens });
        var error = await Assert.ThrowsAsync<AgentSummaryException>(() => AgentCompactionSummaries.GenerateSummaryAsync(
            [new UserMessage("history")], CreateOptions(provider)));
        Assert.Contains(toolCall ? "attempted to call a tool" : "summary is incomplete", error.Message);
    }

    /// <summary>拆分回合没有新增历史时保留旧摘要，同时禁止摘要写入缓存并保留路由标识。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Compact_KeepsPreviousHistoryAndDisablesCacheWrites()
    {
        var provider = new RecordingSummaryProvider((_, options) =>
        {
            Assert.Equal(CacheRetention.None, options.CacheRetention);
            Assert.Equal("summary-session", options.SessionId);
            return new AssistantMessage([new TextContent("turn summary")]) { StopReason = StopReason.EndTurn };
        });
        var preparation = new AgentCompactionPreparation("kept", [], [new UserMessage("turn input")], true, 100,
            "previous summary", AgentCompaction.CreateFileOperations(), new());
        var result = await AgentCompactionSummaries.CompactAsync(preparation, CreateOptions(provider) with
        {
            StreamOptions = new() { CacheRetention = CacheRetention.Long, SessionId = "summary-session" }
        });
        Assert.StartsWith("previous summary\n\n---", result.Summary);
        Assert.Contains("turn summary", result.Summary);
        Assert.Single(provider.Calls);
    }

    private static Model CreateModel(string api) =>
        new()
        {
            Id = "summary-model",
            Name = "Summary Model",
            Api = api,
            Provider = "test",
            ContextWindow = 128_000
        };

    private static AgentSummaryGenerationOptions CreateOptions(RecordingSummaryProvider provider, Model? model = null)
    {
        model ??= CreateModel(provider.Api);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);
        return new AgentSummaryGenerationOptions
        {
            ProviderRegistry = registry,
            Model = model
        };
    }

    private static MessageSessionEntry Entry(string id, ChatMessage message) =>
        new(id, null, Timestamp, message);

    private static string ReadPrompt(LlmContext context) =>
        context.Messages
            .OfType<UserMessage>()
            .SelectMany(static message => message.Content.OfType<TextContent>())
            .Select(static text => text.Text)
            .Single();

    private sealed record SummaryCall(
        string? SystemPrompt,
        string Prompt,
        int? MaxTokens,
        ThinkingLevel? Reasoning);

    private sealed class RecordingSummaryProvider(
        Func<LlmContext, SimpleStreamOptions, AssistantMessage> responseFactory) : IStreamProvider
    {
        private readonly object _gate = new();

        public string Api { get; } = "test-summary-" + Guid.NewGuid().ToString("N");
        public List<SummaryCall> Calls { get; } = [];

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            StreamSimple(model, context, (SimpleStreamOptions)options);

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
        {
            lock (_gate)
            {
                Calls.Add(new SummaryCall(
                    context.SystemPrompt,
                    ReadPrompt(context),
                    options.MaxTokens,
                    options.Reasoning));
            }

            var stream = new AssistantMessageStream();
            stream.Push(new DoneEvent(responseFactory(context, options)));
            return stream;
        }
    }
}
