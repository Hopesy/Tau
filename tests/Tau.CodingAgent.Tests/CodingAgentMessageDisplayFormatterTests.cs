using Tau.AgentCore.Harness;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMessageDisplayFormatterTests
{
    [Fact]
    public void FormatUserMessage_LeavesNormalTextAsUserMessage()
    {
        var messages = CodingAgentMessageDisplayFormatter.FormatUserMessage("plain request");

        var message = Assert.Single(messages);
        Assert.Equal(CodingAgentMessageDisplayFormatter.UserKind, message.Kind);
        Assert.Equal("plain request", message.Text);
    }

    [Fact]
    public void FormatUserMessage_CollapsesSkillInvocationAndKeepsUserMessageSeparate()
    {
        var text = """
            <skill name="reviewer" location="C:\skills\reviewer\SKILL.md">
            References are relative to C:\skills\reviewer.

            Check the diff and explain risks.
            </skill>

            src/app.cs
            """;

        var messages = CodingAgentMessageDisplayFormatter.FormatUserMessage(text);

        Assert.Equal(2, messages.Count);
        Assert.Equal(CodingAgentMessageDisplayFormatter.SkillKind, messages[0].Kind);
        Assert.Equal("[skill] reviewer", messages[0].Text);
        Assert.Equal(CodingAgentMessageDisplayFormatter.UserKind, messages[1].Kind);
        Assert.Equal("src/app.cs", messages[1].Text);
    }

    [Fact]
    public void FormatUserMessage_CanExpandSkillInvocationContent()
    {
        var text = """
            <skill name="reviewer" location="/skills/reviewer/SKILL.md">
            References are relative to /skills/reviewer.

            Check the diff.
            </skill>
            """;

        var messages = CodingAgentMessageDisplayFormatter.FormatUserMessage(text, expanded: true);

        var message = Assert.Single(messages);
        Assert.Equal(CodingAgentMessageDisplayFormatter.SkillKind, message.Kind);
        Assert.Contains("[skill] reviewer", message.Text, StringComparison.Ordinal);
        Assert.Contains("Check the diff.", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseSkillBlock_RejectsPartialXmlInsideOrdinaryMessage()
    {
        var parsed = CodingAgentMessageDisplayFormatter.TryParseSkillBlock(
            "Please read <skill name=\"x\" location=\"y\">inline</skill>",
            out _);

        Assert.False(parsed);
    }

    [Fact]
    public void FormatUserMessage_CollapsesCompactionSummaryWrapper()
    {
        var text = CodingAgentCompactionMessages.CreateSummaryText("summary body");

        var messages = CodingAgentMessageDisplayFormatter.FormatUserMessage(text);

        var message = Assert.Single(messages);
        Assert.Equal(CodingAgentMessageDisplayFormatter.CompactionSummaryKind, message.Kind);
        Assert.Equal("[compaction] Compacted from unknown tokens", message.Text);
    }

    [Fact]
    public void FormatUserMessage_CanExpandCompactionSummaryWrapper()
    {
        var text = CodingAgentCompactionMessages.CreateSummaryText("summary body");

        var messages = CodingAgentMessageDisplayFormatter.FormatUserMessage(text, expanded: true);

        var message = Assert.Single(messages);
        Assert.Equal(CodingAgentMessageDisplayFormatter.CompactionSummaryKind, message.Kind);
        Assert.Contains("[compaction] Compacted from unknown tokens", message.Text, StringComparison.Ordinal);
        Assert.Contains("summary body", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCompactionSummary_RendersTokenCount()
    {
        var rendered = CodingAgentMessageDisplayFormatter.FormatCompactionSummary(
            new CodingAgentCompactionResult("summary body", 5, 1, TokensBefore: 1234));

        Assert.Equal(CodingAgentMessageDisplayFormatter.CompactionSummaryKind, rendered.Kind);
        Assert.Equal("[compaction] Compacted from 1,234 tokens", rendered.Text);
    }

    [Fact]
    public void FormatUserMessage_CollapsesBranchSummaryWrapper()
    {
        var text = CodingAgentCompactionMessages.CreateBranchSummaryText("branch body", "entry-1");

        var messages = CodingAgentMessageDisplayFormatter.FormatUserMessage(text);

        var message = Assert.Single(messages);
        Assert.Equal(CodingAgentMessageDisplayFormatter.BranchSummaryKind, message.Kind);
        Assert.Equal("[branch] Branch summary", message.Text);
    }

    [Fact]
    public void FormatUserMessage_CanExpandBranchSummaryWrapper()
    {
        var text = CodingAgentCompactionMessages.CreateBranchSummaryText("branch body", "entry-1");

        var messages = CodingAgentMessageDisplayFormatter.FormatUserMessage(text, expanded: true);

        var message = Assert.Single(messages);
        Assert.Equal(CodingAgentMessageDisplayFormatter.BranchSummaryKind, message.Kind);
        Assert.Contains("[branch] Branch summary", message.Text, StringComparison.Ordinal);
        Assert.Contains("branch body", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCustomMessage_RendersLabelAndTextContent()
    {
        IReadOnlyList<ContentBlock> content =
        [
            new TextContent("started"),
            new ImageContent("abc", "image/png")
        ];
        var message = new AgentCustomMessage(
            "deploy",
            content,
            true);

        var rendered = CodingAgentMessageDisplayFormatter.FormatCustomMessage(message);

        Assert.Equal(CodingAgentMessageDisplayFormatter.CustomKind, rendered.Kind);
        Assert.Equal(
            """
            [deploy]
            started
            [image:image/png]
            """,
            rendered.Text);
    }

    [Fact]
    public void FormatAssistantMessage_CanCollapseThinkingToDefaultLabel()
    {
        var message = new AssistantMessage(
        [
            new ThinkingContent("internal reasoning"),
            new TextContent("visible answer")
        ]);

        var rendered = CodingAgentMessageDisplayFormatter.FormatAssistantMessage(
            message,
            hideThinkingBlock: true);

        Assert.Equal(
            """
            Thinking...
            visible answer
            """,
            rendered);
        Assert.DoesNotContain("internal reasoning", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatAssistantMessage_UsesCustomHiddenThinkingLabel()
    {
        var message = new AssistantMessage([new ThinkingContent("internal reasoning")]);

        var rendered = CodingAgentMessageDisplayFormatter.FormatAssistantMessage(
            message,
            hideThinkingBlock: true,
            hiddenThinkingLabel: "Pondering...");

        Assert.Equal("Pondering...", rendered);
    }
}
