using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

public sealed class AgentBranchSummariesTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
        "2026-01-01T00:00:00.000Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task CollectEntriesForBranchSummaryAsync_ReturnsOldBranchToCommonAncestor()
    {
        var session = new AgentHarnessSession<SessionMetadata>(new InMemorySessionStorage<SessionMetadata>());
        var root = await session.AppendMessageAsync(new UserMessage("root"));
        var oldAssistant = await session.AppendMessageAsync(new AssistantMessage([new TextContent("old assistant")]));
        var oldLeaf = await session.AppendMessageAsync(new UserMessage("old leaf"));
        await session.MoveToAsync(root);
        var target = await session.AppendMessageAsync(new AssistantMessage([new TextContent("target")]));

        var collected = await AgentBranchSummaries.CollectEntriesForBranchSummaryAsync(session, oldLeaf, target);

        Assert.Equal(root, collected.CommonAncestorId);
        Assert.Equal([oldAssistant, oldLeaf], collected.Entries.Select(static entry => entry.Id));
    }

    [Fact]
    public async Task CollectEntriesForBranchSummaryAsync_ReturnsEmptyWhenThereWasNoOldLeaf()
    {
        var session = new AgentHarnessSession<SessionMetadata>(new InMemorySessionStorage<SessionMetadata>());
        var target = await session.AppendMessageAsync(new UserMessage("target"));

        var collected = await AgentBranchSummaries.CollectEntriesForBranchSummaryAsync(session, null, target);

        Assert.Null(collected.CommonAncestorId);
        Assert.Empty(collected.Entries);
    }

    [Fact]
    public void PrepareBranchEntries_SkipsToolResultsAndCollectsFileOperations()
    {
        var entries = new SessionTreeEntry[]
        {
            Entry("user-1", new UserMessage("request")),
            Entry("assistant-1", new AssistantMessage(
            [
                new ToolCallContent("read-1", "read_file", """{"path":"README.md"}"""),
                new ToolCallContent("edit-1", "edit", """{"path":"src/Edit.cs"}""")
            ])),
            Entry("tool-1", new ToolResultMessage("read-1", [new TextContent("tool output")])),
            new BranchSummarySessionEntry(
                "branch-1",
                "tool-1",
                Timestamp,
                "from-branch",
                "older branch summary",
                new AgentBranchSummaryDetails(["prior-read.md"], ["src/Prior.cs"])),
            new CustomMessageSessionEntry(
                "custom-1",
                "branch-1",
                Timestamp,
                "notice",
                [new TextContent("custom visible")],
                Display: true)
        };

        var preparation = AgentBranchSummaries.PrepareBranchEntries(entries);

        Assert.Equal(["user", "assistant", "branchSummary", "custom"], preparation.Messages.Select(static message => message.Role));
        Assert.Equal(0, preparation.Messages.Count(static message => message is ToolResultMessage));
        var details = AgentCompaction.ComputeFileLists(preparation.FileOperations);
        Assert.Equal(["README.md", "prior-read.md"], details.ReadFiles);
        Assert.Equal(["src/Edit.cs", "src/Prior.cs"], details.ModifiedFiles);
        Assert.True(preparation.TotalTokens > 0);
    }

    [Fact]
    public void PrepareBranchEntries_RespectsTokenBudgetFromRecentEntries()
    {
        var entries = new SessionTreeEntry[]
        {
            Entry("old", new UserMessage(new string('o', 80))),
            Entry("recent", new AssistantMessage([new TextContent("12345678")]))
        };

        var preparation = AgentBranchSummaries.PrepareBranchEntries(entries, tokenBudget: 3);

        var message = Assert.Single(preparation.Messages);
        Assert.Equal("assistant", message.Role);
        Assert.Equal(2, preparation.TotalTokens);
    }

    [Fact]
    public void PrepareBranchEntries_AllowsSummaryEntryToExceedSmallBudgetWhenNoContextSelected()
    {
        var entries = new SessionTreeEntry[]
        {
            new BranchSummarySessionEntry(
                "summary-1",
                null,
                Timestamp,
                "from-branch",
                "12345678")
        };

        var preparation = AgentBranchSummaries.PrepareBranchEntries(entries, tokenBudget: 1);

        var message = Assert.Single(preparation.Messages);
        Assert.Equal("branchSummary", message.Role);
        Assert.Equal(2, preparation.TotalTokens);
    }

    [Fact]
    public void BuildBranchSummaryPrompt_UsesConvertedMessagesAndInstructions()
    {
        var preparation = new AgentBranchPreparation(
            [new UserMessage("hello")],
            AgentCompaction.CreateFileOperations(),
            TotalTokens: 2);

        var defaultPrompt = AgentBranchSummaries.BuildBranchSummaryPrompt(preparation, "keep files");
        var replacementPrompt = AgentBranchSummaries.BuildBranchSummaryPrompt(
            preparation,
            "Only list blockers.",
            replaceInstructions: true);

        Assert.Contains("<conversation>", defaultPrompt, StringComparison.Ordinal);
        Assert.Contains("[User]: hello", defaultPrompt, StringComparison.Ordinal);
        Assert.Contains(AgentBranchSummaries.BranchSummaryPrompt, defaultPrompt, StringComparison.Ordinal);
        Assert.Contains("Additional focus: keep files", defaultPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentBranchSummaries.BranchSummaryPrompt, replacementPrompt, StringComparison.Ordinal);
        Assert.EndsWith("Only list blockers.", replacementPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteBranchSummaryText_AppendsPreambleAndFileMetadata()
    {
        var fileOperations = AgentCompaction.CreateFileOperations();
        fileOperations.Read.Add("README.md");
        fileOperations.Written.Add("src/New.cs");

        var summary = AgentBranchSummaries.CompleteBranchSummaryText("summary body", fileOperations);

        Assert.StartsWith(AgentBranchSummaries.BranchSummaryPreamble, summary, StringComparison.Ordinal);
        Assert.Contains("summary body", summary, StringComparison.Ordinal);
        Assert.Contains("<read-files>\nREADME.md\n</read-files>", summary, StringComparison.Ordinal);
        Assert.Contains("<modified-files>\nsrc/New.cs\n</modified-files>", summary, StringComparison.Ordinal);
    }

    private static MessageSessionEntry Entry(string id, ChatMessage message) =>
        new(id, null, Timestamp, message);
}
