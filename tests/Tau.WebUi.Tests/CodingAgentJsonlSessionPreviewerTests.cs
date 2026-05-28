using Tau.Ai;
using Tau.WebUi.Services;

namespace Tau.WebUi.Tests;

public sealed class CodingAgentJsonlSessionPreviewerTests
{
    private const string Secret = "sk-EXAMPLE_BASE_KEY_99999999";

    [Fact]
    public void Parse_ReturnsHeaderAndMessageTimelineSummary()
    {
        var preview = CodingAgentJsonlSessionPreviewer.Parse(ValidCodingAgentJsonl(), "session.jsonl");

        Assert.Equal("coding-session-1", preview.SessionId);
        Assert.Equal(3, preview.Version);
        Assert.Equal(DateTimeOffset.Parse("2026-05-23T02:00:00+00:00"), preview.Timestamp);
        Assert.Equal("C:\\Users\\zhouh\\Desktop\\Tau", preview.Cwd);
        Assert.Equal("parent-session.jsonl", preview.ParentSession);
        Assert.Equal("session.jsonl", preview.FilePath);
        Assert.Equal(4, preview.EntryCount);
        Assert.Equal(3, preview.MessageCount);
        Assert.Null(preview.Filter.Search);
        Assert.False(preview.Filter.CurrentBranchOnly);
        Assert.Equal(3, preview.Filter.TotalMessageCount);
        Assert.Equal(3, preview.Filter.MatchedMessageCount);
        Assert.Equal(["entry-user", "entry-assistant", "entry-tool"], preview.Filter.MatchedEntryIds);
        Assert.Equal("entry-tool", preview.Tree.LeafEntryId);
        Assert.Equal(1, preview.Tree.RootEntryCount);
        Assert.Equal(0, preview.Tree.BranchPointCount);
        Assert.Equal(4, preview.Tree.BranchEntryCount);
        Assert.Equal(3, preview.Tree.BranchMessageCount);
        Assert.Equal(0, preview.Tree.LabelCount);
        Assert.Equal(3, preview.Tree.EntryTypes["message"]);
        Assert.Equal(1, preview.Tree.EntryTypes["model_change"]);
        Assert.Equal(["entry-user", "entry-model", "entry-assistant", "entry-tool"], preview.Tree.CurrentBranchEntryIds);
        Assert.False(preview.Audit.IsBranched);
        Assert.True(preview.Audit.WillImportTimelineMessagesOnly);
        Assert.False(preview.Audit.WillImportCurrentBranchOnly);
        Assert.Equal(3, preview.Audit.ImportedMessageCount);
        Assert.Equal(1, preview.Audit.NonImportedEntryCount);
        Assert.Equal(3, preview.Audit.CurrentBranchMessageCount);
        Assert.Equal(0, preview.Audit.OffBranchMessageCount);
        Assert.Equal(["entry-user", "entry-model", "entry-assistant", "entry-tool"], preview.Audit.CurrentBranchTimeline.Select(entry => entry.EntryId).ToArray());
        Assert.Equal("openai/gpt-5.4", preview.Audit.CurrentBranchTimeline[1].TextPreview);
        Assert.Empty(preview.Audit.BranchLabels);
        Assert.DoesNotContain(preview.Audit.Warnings, warning => warning.Code == "branch_tree_not_persisted");
        Assert.Contains(preview.Audit.Warnings, warning => warning.Code == "non_message_entries_not_imported_as_messages" && warning.EntryId == "entry-model");
        Assert.Contains(preview.Audit.Warnings, warning => warning.Code == "webchat_import_is_linearized");
        Assert.Equal("conservative-timeline-linearized", preview.ImportStrategy.Strategy);
        Assert.Equal("entry-tool", preview.ImportStrategy.SourceLeafEntryId);
        Assert.False(preview.ImportStrategy.CurrentBranchOnly);
        Assert.True(preview.ImportStrategy.ImportsTimelineMessagesOnly);
        Assert.False(preview.ImportStrategy.PersistsBranchTree);
        Assert.Contains("webchat_import_is_linearized", preview.ImportStrategy.WarningCodes);

        var user = preview.Messages[0];
        Assert.Equal("entry-user", user.EntryId);
        Assert.Null(user.ParentEntryId);
        Assert.Equal("user", user.Role);
        Assert.Equal("hello coding agent", user.TextPreview);
        Assert.Equal("hello coding agent".Length, user.TextLength);
        Assert.Equal(1, user.ContentPartCount);
        Assert.False(user.HasThinking);
        Assert.Equal(0, user.ToolCallCount);
        Assert.Equal(0, user.ImageCount);

        var assistant = preview.Messages[1];
        Assert.Equal("entry-assistant", assistant.EntryId);
        Assert.Equal("entry-model", assistant.ParentEntryId);
        Assert.Equal("assistant", assistant.Role);
        Assert.Equal("done", assistant.TextPreview);
        Assert.True(assistant.HasThinking);
        Assert.Equal(1, assistant.ToolCallCount);
        Assert.Equal(3, assistant.ContentPartCount);

        var toolResult = preview.Messages[2];
        Assert.Equal("entry-tool", toolResult.EntryId);
        Assert.Equal("toolResult", toolResult.Role);
        Assert.Equal("tool-1", toolResult.ToolCallId);
        Assert.True(toolResult.IsError);
        Assert.Equal("not found", toolResult.TextPreview);

        var toolMetadata = Assert.Single(preview.Tree.Entries, entry => entry.EntryId == "entry-tool");
        Assert.Equal("message", toolMetadata.Type);
        Assert.Equal("entry-assistant", toolMetadata.ParentEntryId);
        Assert.Equal(3, toolMetadata.Depth);
        Assert.Equal(0, toolMetadata.ChildCount);
        Assert.True(toolMetadata.IsCurrentLeaf);
        Assert.True(toolMetadata.IsOnCurrentBranch);
    }

    [Fact]
    public void Parse_CanFilterPreviewMessagesToCurrentBranchAndSearch()
    {
        var preview = CodingAgentJsonlSessionPreviewer.Parse(
            BranchedCodingAgentJsonl(),
            filePath: null,
            options: new CodingAgentJsonlPreviewOptions(Search: "after", CurrentBranchOnly: true));

        Assert.Equal(9, preview.EntryCount);
        Assert.Equal(5, preview.MessageCount);
        Assert.Equal("after", preview.Filter.Search);
        Assert.True(preview.Filter.CurrentBranchOnly);
        Assert.Equal(5, preview.Filter.TotalMessageCount);
        Assert.Equal(1, preview.Filter.MatchedMessageCount);
        Assert.Equal(["entry-after-summary"], preview.Filter.MatchedEntryIds);

        var message = Assert.Single(preview.Messages);
        Assert.Equal("entry-after-summary", message.EntryId);
        Assert.Equal("after summary", message.TextPreview);
        Assert.False(preview.Tree.Entries.Single(entry => entry.EntryId == "entry-right").IsOnCurrentBranch);
        Assert.True(preview.Audit.WillImportCurrentBranchOnly);
        Assert.Equal(3, preview.Audit.ImportedMessageCount);
        Assert.Equal(6, preview.Audit.NonImportedEntryCount);
        Assert.Equal(3, preview.Audit.CurrentBranchMessageCount);
        Assert.Equal(2, preview.Audit.OffBranchMessageCount);
        Assert.DoesNotContain(preview.Audit.Warnings, warning => warning.Code == "off_branch_messages_in_timeline");
        Assert.Equal("conservative-current-branch-linearized", preview.ImportStrategy.Strategy);
        Assert.Equal("entry-label", preview.ImportStrategy.SourceLeafEntryId);
        Assert.True(preview.ImportStrategy.CurrentBranchOnly);
        Assert.True(preview.ImportStrategy.ImportsTimelineMessagesOnly);
        Assert.False(preview.ImportStrategy.PersistsBranchTree);
        Assert.DoesNotContain("off_branch_messages_in_timeline", preview.ImportStrategy.WarningCodes);
    }

    [Fact]
    public void Parse_SearchMatchesRoleEntryIdAndToolCallId()
    {
        var byToolId = CodingAgentJsonlSessionPreviewer.Parse(
            ValidCodingAgentJsonl(),
            filePath: null,
            options: new CodingAgentJsonlPreviewOptions(Search: "tool-1"));

        var toolResult = Assert.Single(byToolId.Messages);
        Assert.Equal("entry-tool", toolResult.EntryId);
        Assert.Equal(["entry-tool"], byToolId.Filter.MatchedEntryIds);

        var byRole = CodingAgentJsonlSessionPreviewer.Parse(
            ValidCodingAgentJsonl(),
            filePath: null,
            options: new CodingAgentJsonlPreviewOptions(Search: "assistant"));

        var assistant = Assert.Single(byRole.Messages);
        Assert.Equal("entry-assistant", assistant.EntryId);
    }

    [Fact]
    public void Parse_ReturnsBranchTreeMetadataAndLabelsWithoutChangingTimeline()
    {
        var preview = CodingAgentJsonlSessionPreviewer.Parse(BranchedCodingAgentJsonl());

        Assert.Equal(9, preview.EntryCount);
        Assert.Equal(5, preview.MessageCount);
        Assert.Equal("entry-label", preview.Tree.LeafEntryId);
        Assert.Equal(1, preview.Tree.RootEntryCount);
        Assert.Equal(2, preview.Tree.BranchPointCount);
        Assert.Equal(5, preview.Tree.BranchEntryCount);
        Assert.Equal(3, preview.Tree.BranchMessageCount);
        Assert.Equal(1, preview.Tree.LabelCount);
        Assert.Equal(5, preview.Tree.EntryTypes["message"]);
        Assert.Equal(1, preview.Tree.EntryTypes["branch_summary"]);
        Assert.Equal(3, preview.Tree.EntryTypes["label"]);
        Assert.Equal(["entry-root", "entry-left", "entry-summary", "entry-after-summary", "entry-label"], preview.Tree.CurrentBranchEntryIds);
        Assert.True(preview.Audit.IsBranched);
        Assert.True(preview.Audit.WillImportTimelineMessagesOnly);
        Assert.False(preview.Audit.WillImportCurrentBranchOnly);
        Assert.Equal(5, preview.Audit.ImportedMessageCount);
        Assert.Equal(4, preview.Audit.NonImportedEntryCount);
        Assert.Equal(3, preview.Audit.CurrentBranchMessageCount);
        Assert.Equal(2, preview.Audit.OffBranchMessageCount);
        Assert.Equal(["entry-root", "entry-left", "entry-summary", "entry-after-summary", "entry-label"], preview.Audit.CurrentBranchTimeline.Select(entry => entry.EntryId).ToArray());
        Assert.Equal("branch_summary", preview.Audit.CurrentBranchTimeline[2].Type);
        Assert.Equal("abandoned", preview.Audit.CurrentBranchTimeline[2].TextPreview);
        Assert.False(preview.Audit.CurrentBranchTimeline[2].WillImportAsMessage);
        Assert.True(preview.Audit.CurrentBranchTimeline[^1].IsCurrentLeaf);

        var branchLabel = Assert.Single(preview.Audit.BranchLabels);
        Assert.Equal("entry-root", branchLabel.EntryId);
        Assert.Equal("checkpoint", branchLabel.Label);
        Assert.True(branchLabel.IsOnCurrentBranch);
        Assert.Contains(preview.Audit.Warnings, warning => warning.Code == "branch_tree_not_persisted");
        Assert.Contains(preview.Audit.Warnings, warning => warning.Code == "off_branch_messages_in_timeline");
        Assert.Contains(preview.Audit.Warnings, warning => warning.Code == "non_message_entries_not_imported_as_messages" && warning.EntryId == "entry-label-set");
        Assert.Contains(preview.Audit.Warnings, warning => warning.Code == "webchat_import_is_linearized");

        var root = Assert.Single(preview.Tree.Entries, entry => entry.EntryId == "entry-root");
        Assert.Equal(2, root.ChildCount);
        Assert.Equal("checkpoint", root.Label);
        Assert.Equal(DateTimeOffset.Parse("2026-05-23T02:05:00+00:00"), root.LabelTimestamp);
        Assert.True(root.IsOnCurrentBranch);

        var right = Assert.Single(preview.Tree.Entries, entry => entry.EntryId == "entry-right");
        Assert.False(right.IsOnCurrentBranch);

        var labelEntry = Assert.Single(preview.Tree.Entries, entry => entry.EntryId == "entry-label");
        Assert.Equal("label", labelEntry.Type);
        Assert.Equal(4, labelEntry.Depth);
        Assert.True(labelEntry.IsCurrentLeaf);

        Assert.DoesNotContain(preview.Messages, message => message.EntryId == "entry-summary");
        Assert.Contains(preview.Messages, message => message.EntryId == "entry-right");
    }

    [Fact]
    public void Parse_ResolvesParentAndLabelIdsCaseInsensitivelyLikeTreeStore()
    {
        var preview = CodingAgentJsonlSessionPreviewer.Parse(
            ValidHeader() +
            "{\"type\":\"message\",\"id\":\"Entry-Root\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:01:00+00:00\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"root\"}]}}\n" +
            "{\"type\":\"message\",\"id\":\"entry-child\",\"parentId\":\"ENTRY-ROOT\",\"timestamp\":\"2026-05-23T02:02:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"child\"}]}}\n" +
            "{\"type\":\"label\",\"id\":\"entry-label\",\"parentId\":\"entry-child\",\"timestamp\":\"2026-05-23T02:03:00+00:00\",\"targetId\":\"entry-root\",\"label\":\"Checkpoint\"}\n");

        Assert.Equal(["Entry-Root", "entry-child", "entry-label"], preview.Tree.CurrentBranchEntryIds);
        Assert.Equal(3, preview.Tree.BranchEntryCount);

        var root = Assert.Single(preview.Tree.Entries, entry => entry.EntryId == "Entry-Root");
        Assert.Equal(1, root.ChildCount);
        Assert.Equal("Checkpoint", root.Label);

        var child = Assert.Single(preview.Tree.Entries, entry => entry.EntryId == "entry-child");
        Assert.Equal(1, child.Depth);
        Assert.True(child.IsOnCurrentBranch);
    }

    [Fact]
    public void Parse_TreatsCaseInsensitiveSelfParentAsRootWithoutSelfChild()
    {
        var preview = CodingAgentJsonlSessionPreviewer.Parse(
            ValidHeader() +
            "{\"type\":\"message\",\"id\":\"Entry-Root\",\"parentId\":\"entry-root\",\"timestamp\":\"2026-05-23T02:01:00+00:00\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"root\"}]}}\n");

        Assert.Equal(["Entry-Root"], preview.Tree.CurrentBranchEntryIds);
        Assert.Equal(1, preview.Tree.RootEntryCount);
        Assert.Equal(1, preview.Tree.BranchEntryCount);

        var root = Assert.Single(preview.Tree.Entries);
        Assert.Equal(0, root.Depth);
        Assert.Equal(0, root.ChildCount);
        Assert.True(root.IsCurrentLeaf);
        Assert.True(root.IsOnCurrentBranch);
    }

    [Fact]
    public void Parse_RedactsPreviewTextValuesAndDisabledRedactorPreservesOriginal()
    {
        var jsonl = ValidHeader() +
            $"{{\"type\":\"message\",\"id\":\"entry-user\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:01:00+00:00\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":\"{Secret}\"}}]}}}}\n";

        var preview = CodingAgentJsonlSessionPreviewer.Parse(jsonl, redactor: new TauSecretRedactor(enabled: true));

        var message = Assert.Single(preview.Messages);
        Assert.Equal(TauSecretRedactor.Placeholder, message.TextPreview);
        Assert.Equal(TauSecretRedactor.Placeholder.Length, message.TextLength);

        var unredacted = CodingAgentJsonlSessionPreviewer.Parse(jsonl, redactor: new TauSecretRedactor(enabled: false));
        Assert.Equal(Secret, Assert.Single(unredacted.Messages).TextPreview);
    }

    [Fact]
    public void ParseFile_ReadsSessionFileAndIncludesPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tau-webui-coding-agent-preview-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, ValidCodingAgentJsonl());

            var preview = CodingAgentJsonlSessionPreviewer.ParseFile(path);

            Assert.Equal(path, preview.FilePath);
            Assert.Equal("coding-session-1", preview.SessionId);
            Assert.Equal(3, preview.MessageCount);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Theory]
    [InlineData("{not-json}\n", "invalid_json", 1, "not valid JSON")]
    [InlineData("{\"type\":\"message\",\"id\":\"entry-user\",\"timestamp\":\"2026-05-23T02:01:00+00:00\"}\n", "missing_session_header", 1, "First CodingAgent JSONL line must be a session header")]
    public void Parse_RejectsMalformedHeaderWithStableErrorCode(
        string jsonl,
        string expectedCode,
        int expectedLineNumber,
        string expectedMessage)
    {
        var error = Assert.Throws<CodingAgentJsonlPreviewException>(() => CodingAgentJsonlSessionPreviewer.Parse(jsonl));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedLineNumber, error.LineNumber);
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMalformedEntryJsonWithStableErrorCode()
    {
        var jsonl = ValidHeader() + "{not-json}\n";

        var error = Assert.Throws<CodingAgentJsonlPreviewException>(() => CodingAgentJsonlSessionPreviewer.Parse(jsonl));

        Assert.Equal("invalid_json", error.Code);
        Assert.Equal(2, error.LineNumber);
        Assert.Contains("line 2 is not valid JSON", error.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(error.InnerException);
    }

    [Fact]
    public void Parse_RejectsMessageEntryWithoutMessagePayloadWithStableErrorCode()
    {
        var jsonl = ValidHeader() +
            "{\"type\":\"message\",\"id\":\"entry-user\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:01:00+00:00\"}\n";

        var error = Assert.Throws<CodingAgentJsonlPreviewException>(() => CodingAgentJsonlSessionPreviewer.Parse(jsonl));

        Assert.Equal("missing_message", error.Code);
        Assert.Equal(2, error.LineNumber);
        Assert.Contains("without a message payload", error.Message, StringComparison.Ordinal);
    }

    private static string ValidCodingAgentJsonl() =>
        ValidHeader() +
        "{\"type\":\"message\",\"id\":\"entry-user\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:01:00+00:00\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello coding agent\"}]}}\n" +
        "{\"type\":\"model_change\",\"id\":\"entry-model\",\"parentId\":\"entry-user\",\"timestamp\":\"2026-05-23T02:01:30+00:00\",\"provider\":\"openai\",\"model\":\"gpt-5.4\"}\n" +
        "{\"type\":\"message\",\"id\":\"entry-assistant\",\"parentId\":\"entry-model\",\"timestamp\":\"2026-05-23T02:02:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"plan\"},{\"type\":\"toolCall\",\"id\":\"tool-1\",\"name\":\"read\",\"arguments\":\"{}\"},{\"type\":\"text\",\"text\":\"done\"}]}}\n" +
        "{\"type\":\"message\",\"id\":\"entry-tool\",\"parentId\":\"entry-assistant\",\"timestamp\":\"2026-05-23T02:03:00+00:00\",\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"tool-1\",\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"not found\"}]}}\n";

    private static string BranchedCodingAgentJsonl() =>
        ValidHeader() +
        "{\"type\":\"message\",\"id\":\"entry-root\",\"parentId\":null,\"timestamp\":\"2026-05-23T02:01:00+00:00\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"root\"}]}}\n" +
        "{\"type\":\"message\",\"id\":\"entry-left\",\"parentId\":\"entry-root\",\"timestamp\":\"2026-05-23T02:02:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"left\"}]}}\n" +
        "{\"type\":\"message\",\"id\":\"entry-right\",\"parentId\":\"entry-root\",\"timestamp\":\"2026-05-23T02:03:00+00:00\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"right\"}]}}\n" +
        "{\"type\":\"message\",\"id\":\"entry-right-assistant\",\"parentId\":\"entry-right\",\"timestamp\":\"2026-05-23T02:04:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"right done\"}]}}\n" +
        "{\"type\":\"label\",\"id\":\"entry-label-set\",\"parentId\":\"entry-right-assistant\",\"timestamp\":\"2026-05-23T02:05:00+00:00\",\"targetId\":\"entry-root\",\"label\":\"checkpoint\"}\n" +
        "{\"type\":\"branch_summary\",\"id\":\"entry-summary\",\"parentId\":\"entry-left\",\"timestamp\":\"2026-05-23T02:06:00+00:00\",\"fromId\":\"entry-left\",\"summary\":\"abandoned\"}\n" +
        "{\"type\":\"message\",\"id\":\"entry-after-summary\",\"parentId\":\"entry-summary\",\"timestamp\":\"2026-05-23T02:07:00+00:00\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"after summary\"}]}}\n" +
        "{\"type\":\"label\",\"id\":\"entry-label-clear\",\"parentId\":\"entry-after-summary\",\"timestamp\":\"2026-05-23T02:08:00+00:00\",\"targetId\":\"entry-after-summary\",\"label\":\"temp\"}\n" +
        "{\"type\":\"label\",\"id\":\"entry-label\",\"parentId\":\"entry-after-summary\",\"timestamp\":\"2026-05-23T02:09:00+00:00\",\"targetId\":\"entry-after-summary\",\"label\":\"\"}\n";

    private static string ValidHeader() =>
        "{\"type\":\"session\",\"version\":3,\"id\":\"coding-session-1\",\"timestamp\":\"2026-05-23T02:00:00+00:00\",\"cwd\":\"C:\\\\Users\\\\zhouh\\\\Desktop\\\\Tau\",\"parentSession\":\"parent-session.jsonl\"}\n";
}
