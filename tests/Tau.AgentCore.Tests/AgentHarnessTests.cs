using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.AgentCore.Platform;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Streaming;

namespace Tau.AgentCore.Tests;

public sealed class AgentHarnessTests
{
    [Fact]
    public async Task PromptAsync_AppendsPromptAndAssistantToSessionAndEmitsEvents()
    {
        var provider = new CapturingProvider("done");
        var harness = CreateHarness(provider, systemPromptFactory: context =>
            $"system with {context.Resources.Skills?.Count ?? 0} skills");
        var events = new List<object>();
        using var subscription = harness.Subscribe(events.Add);

        var assistant = await harness.PromptAsync("hello").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("done", ReadText(assistant.Content));
        Assert.NotNull(provider.LastContext);
        Assert.Equal("system with 1 skills", provider.LastContext.Value.SystemPrompt);
        var request = Assert.Single(provider.LastContext.Value.Messages);
        Assert.Equal("hello", ReadText(Assert.IsType<UserMessage>(request).Content));

        var branch = await harness.Session.GetBranchAsync();
        Assert.Equal(["message", "message"], branch.Select(static entry => entry.Type));
        Assert.Equal("hello", ReadText(Assert.IsType<UserMessage>(Assert.IsType<MessageSessionEntry>(branch[0]).Message).Content));
        Assert.Equal("done", ReadText(Assert.IsType<AssistantMessage>(Assert.IsType<MessageSessionEntry>(branch[1]).Message).Content));

        Assert.Contains(events, static evt => evt is AgentEndEvent);
        Assert.Contains(events, static evt => evt is AgentHarnessSavePointEvent);
        Assert.Contains(events, static evt => evt is AgentHarnessSettledEvent { NextTurnCount: 0 });
        Assert.Equal(AgentHarnessPhase.Idle, harness.Phase);
    }

    [Fact]
    public async Task SkillAndPromptTemplateInvocationsUseHarnessResources()
    {
        var provider = new CapturingProvider("skill done", "template done");
        var harness = CreateHarness(provider);

        await harness.SkillAsync("review", "focus security").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(provider.LastContext);
        var skillPrompt = ReadText(Assert.IsType<UserMessage>(provider.LastContext.Value.Messages[^1]).Content);
        Assert.Contains("""<skill name="review" location="C:\skills\review\SKILL.md">""", skillPrompt, StringComparison.Ordinal);
        Assert.Contains("Read code carefully.", skillPrompt, StringComparison.Ordinal);
        Assert.Contains("focus security", skillPrompt, StringComparison.Ordinal);

        await harness.PromptFromTemplateAsync("fix", ["README.md", "tests"]).WaitAsync(TimeSpan.FromSeconds(5));
        var templatePrompt = ReadText(Assert.IsType<UserMessage>(provider.LastContext!.Value.Messages[^1]).Content);
        Assert.Equal("Fix README.md then run tests.", templatePrompt);
    }

    [Fact]
    public async Task CompactAsync_AppendsCompactionEntryAndRebuildsContextFromSummary()
    {
        var provider = new CapturingProvider("compact summary");
        var harness = CreateHarness(provider);
        await harness.AppendMessageAsync(new UserMessage("old request"));
        await harness.AppendMessageAsync(new AssistantMessage([new TextContent("old answer")]));
        var firstKept = await harness.Session.AppendMessageAsync(new UserMessage("recent request"));
        var events = new List<object>();
        using var subscription = harness.Subscribe(events.Add);

        var result = await harness.CompactAsync(
            settings: new AgentCompactionSettings(KeepRecentTokens: 1))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("compact summary", result.Summary);
        Assert.Equal(firstKept, result.FirstKeptEntryId);
        Assert.True(result.TokensBefore > 0);
        var compaction = Assert.IsType<CompactionSessionEntry>(
            (await harness.Session.GetEntriesAsync()).Last(static entry => entry.Type == "compaction"));
        Assert.Equal("compact summary", compaction.Summary);
        Assert.Equal(firstKept, compaction.FirstKeptEntryId);
        Assert.Contains(events, static evt => evt is AgentHarnessSessionCompactEvent);

        var context = await harness.Session.BuildContextAsync();
        Assert.Equal(["compactionSummary", "user"], context.Messages.Select(static message => message.Role));
        Assert.Equal("compact summary", Assert.IsType<AgentCompactionSummaryMessage>(context.Messages[0]).Summary);
    }

    [Fact]
    public async Task NavigateTreeAsync_SummarizesOldBranchAndPreservesFromId()
    {
        var provider = new CapturingProvider("branch summary");
        var harness = CreateHarness(provider);
        var root = await harness.Session.AppendMessageAsync(new UserMessage("root prompt"));
        await harness.Session.AppendMessageAsync(new AssistantMessage([new TextContent("main answer")]));
        await harness.Session.AppendMessageAsync(new UserMessage("branch request"));
        await harness.Session.AppendMessageAsync(new AssistantMessage(
        [
            new TextContent("branch work"),
            new ToolCallContent("call-1", "write_file", """{"path":"src/Branch.cs"}""")
        ]));
        var events = new List<object>();
        using var subscription = harness.Subscribe(events.Add);

        var result = await harness.NavigateTreeAsync(
            root,
            summarize: true)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Cancelled);
        Assert.Equal("root prompt", result.EditorText);
        Assert.NotNull(result.SummaryEntry);
        Assert.Null(result.SummaryEntry.ParentId);
        Assert.Equal("root", result.SummaryEntry.FromId);
        Assert.Contains("branch summary", result.SummaryEntry.Summary, StringComparison.Ordinal);
        Assert.Equal("src/Branch.cs", Assert.IsType<AgentBranchSummaryDetails>(result.SummaryEntry.Details).ModifiedFiles.Single());
        Assert.Equal(result.SummaryEntry.Id, await harness.Session.GetLeafIdAsync());
        var context = await harness.Session.BuildContextAsync();
        Assert.Equal("branchSummary", Assert.Single(context.Messages).Role);
        Assert.Contains(events, static evt => evt is AgentHarnessSessionTreeEvent);
    }

    [Fact]
    public async Task SetModelThinkingAndActiveToolsPersistSessionState()
    {
        var provider = new CapturingProvider("done");
        var tool = new DelegateAgentTool(
            "echo",
            "Echo",
            "Echoes.",
            Json("""{"type":"object"}"""),
            (context, _) => Task.FromResult(new ToolResult([new TextContent(context.ToolName)])));
        var harness = CreateHarness(provider, tools: [tool]);
        var nextModel = Model with { Id = "next-model", Provider = "next-provider" };

        await harness.SetModelAsync(nextModel);
        await harness.SetThinkingLevelAsync(ThinkingLevel.High);
        await harness.SetActiveToolsAsync(["echo"]);

        var context = await harness.Session.BuildContextAsync();
        Assert.Equal(new SessionModelReference("next-provider", "next-model"), context.Model);
        Assert.Equal("high", context.ThinkingLevel);
        Assert.Equal(["echo"], context.ActiveToolNames);
    }

    private static AgentHarness<SessionMetadata> CreateHarness(
        CapturingProvider provider,
        Func<AgentHarnessSystemPromptContext<SessionMetadata>, string>? systemPromptFactory = null,
        IReadOnlyList<IAgentTool>? tools = null)
    {
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);
        return new AgentHarness<SessionMetadata>(new AgentHarnessOptions<SessionMetadata>
        {
            Session = new AgentHarnessSession<SessionMetadata>(new InMemorySessionStorage<SessionMetadata>()),
            ProviderRegistry = registry,
            Model = Model,
            Tools = tools ?? [],
            Resources = new AgentHarnessResources(
                Skills:
                [
                    new AgentHarnessSkill(
                        "review",
                        "Review code.",
                        "Read code carefully.",
                        @"C:\skills\review\SKILL.md")
                ],
                PromptTemplates:
                [
                    new AgentPromptTemplate(
                        "fix",
                        "Fix file",
                        "Fix $1 then run $2.")
                ]),
            SystemPrompt = "system",
            SystemPromptFactory = systemPromptFactory is null
                ? null
                : (context, _) => Task.FromResult(systemPromptFactory(context))
        });
    }

    private static Model Model { get; } = new()
    {
        Id = "test-model",
        Name = "Test model",
        Api = "capture",
        Provider = "test",
        ContextWindow = 128_000,
        MaxOutputTokens = 4_096
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ReadText(IReadOnlyList<ContentBlock> content) =>
        string.Join("", content.OfType<TextContent>().Select(static text => text.Text));

    private sealed class CapturingProvider(params string[] responses) : IStreamProvider
    {
        private readonly Queue<string> _responses = new(responses);

        public string Api => "capture";
        public LlmContext? LastContext { get; private set; }

        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
            throw new NotSupportedException();

        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
        {
            LastContext = context;
            var stream = new AssistantMessageStream();
            var text = _responses.Count == 0 ? "done" : _responses.Dequeue();
            stream.Push(new DoneEvent(new AssistantMessage([new TextContent(text)])));
            return stream;
        }
    }
}
