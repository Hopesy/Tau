using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

public sealed class AgentHarnessSessionTests
{
    [Fact]
    public async Task AppendMessageAsync_BuildsContextInOrder()
    {
        var session = CreateSession();

        await session.AppendMessageAsync(new UserMessage("one"));
        await session.AppendMessageAsync(new AssistantMessage([new TextContent("two")]));

        var context = await session.BuildContextAsync();

        Assert.Equal(["user", "assistant"], context.Messages.Select(static message => message.Role));
    }

    [Fact]
    public async Task BuildContextAsync_TracksModelThinkingLevelAndActiveTools()
    {
        var session = CreateSession();

        await session.AppendMessageAsync(new UserMessage("one"));
        await session.AppendModelChangeAsync("openai", "gpt-5.4");
        await session.AppendThinkingLevelChangeAsync("high");
        await session.AppendActiveToolsChangeAsync(["read_file", "shell"]);

        var context = await session.BuildContextAsync();

        Assert.Equal("high", context.ThinkingLevel);
        Assert.Equal(new SessionModelReference("openai", "gpt-5.4"), context.Model);
        Assert.Equal(["read_file", "shell"], context.ActiveToolNames);
    }

    [Fact]
    public async Task MoveToAsync_SupportsBranchingFromEarlierLeaf()
    {
        var session = CreateSession();
        var user1 = await session.AppendMessageAsync(new UserMessage("one"));
        var assistant1 = await session.AppendMessageAsync(new AssistantMessage([new TextContent("two")]));
        await session.AppendMessageAsync(new UserMessage("three"));

        await session.MoveToAsync(user1);
        await session.AppendMessageAsync(new AssistantMessage([new TextContent("branched")]));

        var branch = await session.GetBranchAsync();
        Assert.Contains(branch, entry => entry.Id == user1);
        Assert.DoesNotContain(branch, entry => entry.Id == assistant1);
        Assert.Equal(["user", "assistant"], (await session.BuildContextAsync()).Messages.Select(static message => message.Role));
    }

    [Fact]
    public async Task MoveToAsync_SupportsMovingLeafToRoot()
    {
        var session = CreateSession();
        await session.AppendMessageAsync(new UserMessage("one"));

        await session.MoveToAsync(null);

        Assert.Null(await session.GetLeafIdAsync());
        Assert.Empty((await session.BuildContextAsync()).Messages);
    }

    [Fact]
    public async Task MoveToAsync_WithSummary_AppendsBranchSummaryEntry()
    {
        var session = CreateSession();
        var user1 = await session.AppendMessageAsync(new UserMessage("one"));

        var summaryId = await session.MoveToAsync(user1, new SessionBranchSummary("summary text"));

        Assert.NotNull(summaryId);
        var summaryEntry = Assert.IsType<BranchSummarySessionEntry>(await session.GetEntryAsync(summaryId!));
        Assert.Equal(user1, summaryEntry.ParentId);
        Assert.Equal(user1, summaryEntry.FromId);
    }

    [Fact]
    public async Task SessionNamesLabelsAndInfoEntriesDoNotAffectContext()
    {
        var session = CreateSession();
        var user1 = await session.AppendMessageAsync(new UserMessage("one"));

        await session.AppendLabelAsync(user1, "checkpoint");
        await session.AppendSessionNameAsync(" hello\nworld\r\nagain ");

        var entries = await session.GetEntriesAsync();
        Assert.Contains(entries, static entry => entry.Type == "label");
        Assert.Contains(entries, static entry => entry.Type == "session_info");
        Assert.Equal("checkpoint", await session.GetLabelAsync(user1));
        Assert.Equal("hello world again", await session.GetSessionNameAsync());
        Assert.Single((await session.BuildContextAsync()).Messages);
    }

    [Fact]
    public async Task AppendLabelAsync_RejectsMissingEntries()
    {
        var session = CreateSession();

        var ex = await Assert.ThrowsAsync<SessionException>(() => session.AppendLabelAsync("missing", "checkpoint"));

        Assert.Equal("not_found", ex.Code);
        Assert.Equal("Entry missing not found", ex.Message);
    }

    [Fact]
    public async Task BuildContextAsync_WithCompactionKeepsSummaryAndRetainedEntries()
    {
        var session = CreateSession();
        await session.AppendMessageAsync(new UserMessage("old"));
        var firstKept = await session.AppendMessageAsync(new AssistantMessage([new TextContent("kept")]));
        await session.AppendMessageAsync(new UserMessage("recent"));
        await session.AppendCompactionAsync("compact summary", firstKept, tokensBefore: 123);
        await session.AppendMessageAsync(new AssistantMessage([new TextContent("after")]));

        var context = await session.BuildContextAsync();

        Assert.Equal(["compactionSummary", "assistant", "user", "assistant"], context.Messages.Select(static message => message.Role));
        var summary = Assert.IsType<AgentCompactionSummaryMessage>(context.Messages[0]);
        Assert.Equal("compact summary", summary.Summary);
        Assert.Equal(123, summary.TokensBefore);
        Assert.Equal("kept", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<AssistantMessage>(context.Messages[1]).Content)).Text);
        Assert.Equal("recent", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(context.Messages[2]).Content)).Text);

        var llmMessages = AgentHarnessMessages.ConvertToLlm(context.Messages);
        var llmSummary = Assert.IsType<UserMessage>(llmMessages[0]);
        Assert.Contains(AgentHarnessMessages.CompactionSummaryPrefix, Assert.IsType<TextContent>(Assert.Single(llmSummary.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildContextAsync_WithCustomMessageAndBranchSummaryConvertsToLlmMessages()
    {
        var session = CreateSession();
        var user1 = await session.AppendMessageAsync(new UserMessage("one"));
        await session.MoveToAsync(user1, new SessionBranchSummary("branch summary"));
        await session.AppendCustomEntryAsync("internal", "hidden");
        await session.AppendCustomMessageEntryAsync("visible", "custom body", display: true);

        var context = await session.BuildContextAsync();

        Assert.Equal(["user", "branchSummary", "custom"], context.Messages.Select(static message => message.Role));
        var custom = Assert.IsType<AgentCustomMessage>(context.Messages[2]);
        Assert.Equal("visible", custom.CustomType);
        Assert.True(custom.Display);

        var llmMessages = AgentHarnessMessages.ConvertToLlm(context.Messages);
        Assert.Equal(["user", "user", "user"], llmMessages.Select(static message => message.Role));
        Assert.Contains(
            AgentHarnessMessages.BranchSummaryPrefix,
            Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(llmMessages[1]).Content)).Text,
            StringComparison.Ordinal);
        Assert.Equal(
            "custom body",
            Assert.IsType<TextContent>(Assert.Single(Assert.IsType<UserMessage>(llmMessages[2]).Content)).Text);
    }

    private static AgentHarnessSession<SessionMetadata> CreateSession() =>
        new(new InMemorySessionStorage<SessionMetadata>());
}
