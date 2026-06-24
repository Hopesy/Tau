using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

public sealed class AgentCompactionTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
        "2026-01-01T00:00:00.000Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void ExtractFileOperationsFromMessage_TracksUpstreamAndTauToolNames()
    {
        var fileOperations = AgentCompaction.CreateFileOperations();
        var message = new AssistantMessage(
        [
            new ToolCallContent("read-1", "read", """{"path":"README.md"}"""),
            new ToolCallContent("read-2", "read_file", """{"file_path":"src/Program.cs"}"""),
            new ToolCallContent("write-1", "write", """{"path":"src/New.cs"}"""),
            new ToolCallContent("edit-1", "edit_file", """{"path":"src/Edit.cs"}"""),
            new ToolCallContent("bad-json", "read", "not json"),
            new ToolCallContent("missing-path", "write_file", """{"content":"x"}"""),
            new ToolCallContent("shell-1", "shell", """{"path":"ignored"}""")
        ]);

        AgentCompaction.ExtractFileOperationsFromMessage(message, fileOperations);

        Assert.Equal(["README.md", "src/Program.cs"], fileOperations.Read.Order(StringComparer.Ordinal));
        Assert.Equal(["src/New.cs"], fileOperations.Written.Order(StringComparer.Ordinal));
        Assert.Equal(["src/Edit.cs"], fileOperations.Edited.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ComputeFileLists_RemovesModifiedFilesFromReadOnlyList()
    {
        var fileOperations = AgentCompaction.CreateFileOperations();
        fileOperations.Read.Add("README.md");
        fileOperations.Read.Add("src/Edit.cs");
        fileOperations.Written.Add("src/New.cs");
        fileOperations.Edited.Add("src/Edit.cs");

        var details = AgentCompaction.ComputeFileLists(fileOperations);

        Assert.Equal(["README.md"], details.ReadFiles);
        Assert.Equal(["src/Edit.cs", "src/New.cs"], details.ModifiedFiles);
    }

    [Fact]
    public void FormatFileOperations_EmitsSummaryMetadataTags()
    {
        var text = AgentCompaction.FormatFileOperations(
            ["README.md"],
            ["src/Edit.cs", "src/New.cs"]);

        Assert.Equal(
            """


            <read-files>
            README.md
            </read-files>

            <modified-files>
            src/Edit.cs
            src/New.cs
            </modified-files>
            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            text);
        Assert.Equal(string.Empty, AgentCompaction.FormatFileOperations([], []));
    }

    [Fact]
    public void SerializeConversation_RendersMessagesForSummarization()
    {
        var longOutput = new string('x', 2_005);
        var serialized = AgentCompaction.SerializeConversation(
        [
            new UserMessage([new TextContent("hello"), new ImageContent("abc", "image/png")]),
            new AssistantMessage(
            [
                new ThinkingContent("plan"),
                new TextContent("done"),
                new ToolCallContent("call-1", "read_file", """{"path":"README.md","limit":10}""")
            ]),
            new ToolResultMessage("call-1", [new TextContent(longOutput)])
        ]);

        Assert.Contains("[User]: hello", serialized, StringComparison.Ordinal);
        Assert.Contains("[Assistant thinking]: plan", serialized, StringComparison.Ordinal);
        Assert.Contains("[Assistant]: done", serialized, StringComparison.Ordinal);
        Assert.Contains("""read_file(path="README.md", limit=10)""", serialized, StringComparison.Ordinal);
        Assert.Contains("[... 5 more characters truncated]", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 2_005), serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void EstimateTokens_UsesHarnessMessageHeuristics()
    {
        Assert.Equal(
            1_202,
            AgentCompaction.EstimateTokens(new UserMessage([new TextContent("12345678"), new ImageContent("abc", "image/png")])));
        Assert.Equal(
            2,
            AgentCompaction.EstimateTokens(new AgentBashExecutionMessage("abcd", "efgh", 0, Cancelled: false, Truncated: false)));
        Assert.Equal(
            2,
            AgentCompaction.EstimateTokens(new AgentBranchSummaryMessage("12345", "from", Timestamp)));
        Assert.Equal(
            3,
            AgentCompaction.EstimateTokens(new AgentCompactionSummaryMessage("123456789", 99, Timestamp)));
    }

    [Fact]
    public void EstimateContextTokens_UsesLastAssistantUsageAndTrailingEstimate()
    {
        var messages = new ChatMessage[]
        {
            new UserMessage("old"),
            new AssistantMessage([new TextContent("ignored")])
            {
                Usage = new Usage(1, 1),
                StopReason = StopReason.Error
            },
            new AssistantMessage([new TextContent("used")])
            {
                Usage = new Usage(100, 50, 5, 6),
                StopReason = StopReason.EndTurn
            },
            new UserMessage("12345678")
        };

        var estimate = AgentCompaction.EstimateContextTokens(messages);

        Assert.Equal(163, estimate.Tokens);
        Assert.Equal(161, estimate.UsageTokens);
        Assert.Equal(2, estimate.TrailingTokens);
        Assert.Equal(2, estimate.LastUsageIndex);
    }

    [Fact]
    public void ShouldCompact_UsesReserveTokenThreshold()
    {
        Assert.False(AgentCompaction.ShouldCompact(83_616, 100_000));
        Assert.True(AgentCompaction.ShouldCompact(83_617, 100_000));
        Assert.False(AgentCompaction.ShouldCompact(
            99_999,
            100_000,
            new AgentCompactionSettings(Enabled: false)));
    }

    [Fact]
    public void FindCutPoint_SkipsToolResultsAndDetectsSplitTurns()
    {
        var nonSplitEntries = new SessionTreeEntry[]
        {
            Entry("u0", new UserMessage("old")),
            Entry("a0", new AssistantMessage([new TextContent("ok")])),
            Entry("t0", new ToolResultMessage("call-1", [new TextContent(new string('x', 40))])),
            Entry("u1", new UserMessage("next")),
            Entry("a1", new AssistantMessage([new TextContent("done")]))
        };

        var nonSplit = AgentCompaction.FindCutPoint(nonSplitEntries, 0, nonSplitEntries.Length, keepRecentTokens: 5);

        Assert.Equal(3, nonSplit.FirstKeptEntryIndex);
        Assert.Equal(-1, nonSplit.TurnStartIndex);
        Assert.False(nonSplit.IsSplitTurn);

        var splitEntries = new SessionTreeEntry[]
        {
            Entry("u0", new UserMessage("old")),
            Entry("a0", new AssistantMessage([new TextContent("ok")])),
            Entry("t0", new ToolResultMessage("call-1", [new TextContent(new string('x', 40))])),
            Entry("a1", new AssistantMessage([new TextContent("done")]))
        };

        var split = AgentCompaction.FindCutPoint(splitEntries, 0, splitEntries.Length, keepRecentTokens: 5);

        Assert.Equal(3, split.FirstKeptEntryIndex);
        Assert.Equal(0, split.TurnStartIndex);
        Assert.True(split.IsSplitTurn);

        var trailingToolResultEntries = new SessionTreeEntry[]
        {
            Entry("u0", new UserMessage("old")),
            Entry("a0", new AssistantMessage([new TextContent("ok")])),
            Entry("t0", new ToolResultMessage("call-1", [new TextContent(new string('x', 40))]))
        };

        var trailingToolResult = AgentCompaction.FindCutPoint(
            trailingToolResultEntries,
            0,
            trailingToolResultEntries.Length,
            keepRecentTokens: 5);

        Assert.Equal(0, trailingToolResult.FirstKeptEntryIndex);
        Assert.False(trailingToolResult.IsSplitTurn);
    }

    [Fact]
    public void PrepareCompaction_ReturnsNullForEmptyOrTrailingCompaction()
    {
        Assert.Null(AgentCompaction.PrepareCompaction([]));
        Assert.Null(AgentCompaction.PrepareCompaction(
        [
            new CompactionSessionEntry("c0", null, Timestamp, "summary", "u0", 42)
        ]));
    }

    [Fact]
    public void PrepareCompaction_SplitsCurrentTurnAndCollectsFileOperations()
    {
        var entries = new SessionTreeEntry[]
        {
            Entry("u0", new UserMessage("old request")),
            Entry("a0", new AssistantMessage([new ToolCallContent("read-1", "read", """{"path":"README.md"}""")])),
            Entry("u1", new UserMessage("current turn prefix")),
            Entry("a1", new AssistantMessage([new TextContent("recent")]))
        };

        var preparation = AgentCompaction.PrepareCompaction(entries, new AgentCompactionSettings(KeepRecentTokens: 1));

        Assert.NotNull(preparation);
        Assert.Equal("a1", preparation.FirstKeptEntryId);
        Assert.True(preparation.IsSplitTurn);
        Assert.Equal(["user", "assistant"], preparation.MessagesToSummarize.Select(static message => message.Role));
        Assert.Equal(["user"], preparation.TurnPrefixMessages.Select(static message => message.Role));

        var details = AgentCompaction.ComputeFileLists(preparation.FileOperations);
        Assert.Equal(["README.md"], details.ReadFiles);
        Assert.Empty(details.ModifiedFiles);
    }

    [Fact]
    public void PrepareCompaction_UsesPreviousCompactionBoundaryAndDetails()
    {
        var entries = new SessionTreeEntry[]
        {
            Entry("old", new UserMessage("old")),
            new CompactionSessionEntry(
                "c0",
                "old",
                Timestamp,
                "previous summary",
                "u1",
                90,
                new AgentCompactionDetails(["prior-read.md"], ["src/Prior.cs"])),
            Entry("u1", new UserMessage("kept request")),
            Entry("a1", new AssistantMessage([new ToolCallContent("read-1", "read_file", """{"path":"src/New.cs"}""")])),
            Entry("u2", new UserMessage("current turn prefix")),
            Entry("a2", new AssistantMessage([new TextContent("recent")]))
        };

        var preparation = AgentCompaction.PrepareCompaction(entries, new AgentCompactionSettings(KeepRecentTokens: 1));

        Assert.NotNull(preparation);
        Assert.Equal("previous summary", preparation.PreviousSummary);
        Assert.Equal("a2", preparation.FirstKeptEntryId);
        Assert.Equal(["user", "assistant"], preparation.MessagesToSummarize.Select(static message => message.Role));

        var details = AgentCompaction.ComputeFileLists(preparation.FileOperations);
        Assert.Equal(["prior-read.md", "src/New.cs"], details.ReadFiles);
        Assert.Equal(["src/Prior.cs"], details.ModifiedFiles);
    }

    private static MessageSessionEntry Entry(string id, ChatMessage message) =>
        new(id, null, Timestamp, message);
}
