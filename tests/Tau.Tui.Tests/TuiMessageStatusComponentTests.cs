using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiMessageStatusComponentTests
{
    [Fact]
    public void MessageArea_RendersWrappedMessagesWithContinuationIndent()
    {
        var area = new TuiMessageArea(
            [
                new TuiMessage(TuiMessageRole.Assistant, "alpha beta gamma"),
            ]);

        var lines = area.Render(16);

        Assert.Equal(["tau> alpha beta ", "     gamma      "], lines);
        Assert.All(lines, line => Assert.Equal(16, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void MessageArea_TakesLastVisibleLinesForBottomAnchoredTranscript()
    {
        var area = new TuiMessageArea(
            [
                new TuiMessage(TuiMessageRole.User, "one"),
                new TuiMessage(TuiMessageRole.Assistant, "two"),
                new TuiMessage(TuiMessageRole.Tool, "three"),
            ],
            maxVisibleLines: 2);

        var lines = area.Render(20);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("tau> two", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("tool> three", lines[1], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal(20, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void MessageArea_StaticRendererKeepsMultilineContentInOrder()
    {
        var lines = TuiMessageArea.RenderMessages(
            [
                new TuiMessage(TuiMessageRole.User, "first line\nsecond line"),
            ],
            width: 18);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("you> first line", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("     second line", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void MessageArea_RendersCustomAndSkillRolesWithStablePrefixes()
    {
        var lines = TuiMessageArea.RenderMessages(
            [
                new TuiMessage(TuiMessageRole.Skill, "[skill] reviewer"),
                new TuiMessage(TuiMessageRole.Custom, "[deploy]\nstarted"),
                new TuiMessage(TuiMessageRole.BranchSummary, "[branch] Branch summary"),
                new TuiMessage(TuiMessageRole.CompactionSummary, "[compaction] Compacted from 1,234 tokens"),
            ],
            width: 52);

        Assert.Equal(5, lines.Count);
        Assert.StartsWith("skill> [skill] reviewer", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("custom> [deploy]", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("        started", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("branch> [branch] Branch summary", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("compaction> [compaction] Compacted from 1,234 tokens", lines[4], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal(52, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void StatusBar_AlignsRightSegmentWithoutOverlappingLeft()
    {
        var line = TuiStatusBar.RenderLine("model: openai/gpt-5", "tokens 12k", 32);

        Assert.Equal("model: openai/gpt-5   tokens 12k", line);
        Assert.Equal(32, TuiText.VisibleWidth(line));
    }

    [Fact]
    public void StatusBar_TruncatesLeftBeforeRightWhenNarrow()
    {
        var line = TuiStatusBar.RenderLine("long-left-segment", "ready", 12);

        Assert.Equal("long-l ready", line);
        Assert.Equal(12, TuiText.VisibleWidth(line));
    }

    [Fact]
    public void StatusBar_RightOnlyIsRightAligned()
    {
        var bar = new TuiStatusBar(right: "ready");

        Assert.Equal(["     ready"], bar.Render(10));
    }

    [Fact]
    public void StatusBar_RendersMultipleLinesWithIndependentAlignment()
    {
        var bar = new TuiStatusBar("cwd", "");
        bar.SetLines(
            [
                new TuiStatusBarLine("~/work", ""),
                new TuiStatusBarLine("in1.5k out200", "gpt-5"),
                new TuiStatusBarLine("build ok", "")
            ]);

        var lines = bar.Render(20);

        Assert.Equal(
            [
                "~/work              ",
                "in1.5k out200  gpt-5",
                "build ok            "
            ],
            lines);
        Assert.Equal(3, bar.LineCount);
        Assert.All(lines, line => Assert.Equal(20, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void MessageArea_AgentThemeUsesCompactRoleMarkersAndStableWidths()
    {
        var lines = TuiMessageArea.RenderMessages(
            [
                new TuiMessage(TuiMessageRole.User, "hello"),
                new TuiMessage(TuiMessageRole.Assistant, "answer"),
                new TuiMessage(TuiMessageRole.Tool, "build")
            ],
            width: 24,
            displayOptions: TuiMessageDisplayOptions.Agent);

        Assert.Contains("→", lines[0], StringComparison.Ordinal);
        Assert.Contains("◆", lines[1], StringComparison.Ordinal);
        Assert.Contains("⚒", lines[2], StringComparison.Ordinal);
        Assert.DoesNotContain("you>", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("tau>", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("tool>", lines[2], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal(24, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void MessageArea_AgentThemeSeparatesNewUserTurn()
    {
        var lines = TuiMessageArea.RenderMessages(
            [
                new TuiMessage(TuiMessageRole.User, "first"),
                new TuiMessage(TuiMessageRole.Assistant, "answer"),
                new TuiMessage(TuiMessageRole.User, "follow up")
            ],
            width: 24,
            displayOptions: TuiMessageDisplayOptions.Agent);

        Assert.Equal(new string(' ', 24), lines[2]);
        Assert.Contains("follow up", lines[3], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal(24, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void MessageArea_AgentThemeFormatsAssistantMarkdownAndBoldUserText()
    {
        var lines = TuiMessageArea.RenderMessages(
            [
                new TuiMessage(TuiMessageRole.User, "Please inspect **this**"),
                new TuiMessage(TuiMessageRole.Assistant, "# Answer\n\n```cs\nvar x = 1;\n```")
            ],
            width: 42,
            displayOptions: TuiMessageDisplayOptions.Agent);

        Assert.Contains(lines, line => line.Contains("\u001b[1m", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("\u001b[48;5;238m", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Answer", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("# Answer", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("var", StringComparison.Ordinal) && line.Contains("x", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Equal(42, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void MessageArea_AgentThemeStylesWelcomeTitleWithoutLogPrefix()
    {
        var lines = TuiMessageArea.RenderMessages(
            [
                new TuiMessage(TuiMessageRole.System, "Tau — Coding Agent"),
                new TuiMessage(TuiMessageRole.System, "Type your message")
            ],
            width: 32,
            displayOptions: TuiMessageDisplayOptions.Agent);

        Assert.Contains("Tau — Coding Agent", lines[0], StringComparison.Ordinal);
        Assert.Contains("\u001b[1;38;5;215m", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("•", lines[0], StringComparison.Ordinal);
        Assert.Contains("\u001b[2;90m", lines[1], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal(32, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void StatusBar_AgentThemeStylesSegmentsWithoutChangingLayoutWidth()
    {
        var bar = new TuiStatusBar("~/repo (main)", "gpt-5.4", TuiStatusBarTheme.Agent);

        var line = Assert.Single(bar.Render(32));

        Assert.Contains("\u001b[90m", line, StringComparison.Ordinal);
        Assert.Contains("\u001b[36m", line, StringComparison.Ordinal);
        Assert.Contains("~/repo (main)", line, StringComparison.Ordinal);
        Assert.Contains("gpt-5.4", line, StringComparison.Ordinal);
        Assert.Equal(32, TuiText.VisibleWidth(line));
    }

    [Fact]
    public void ToolAndBashAgentThemesExposeLifecycleMarkers()
    {
        var tool = new TuiToolExecution("read_file", "call-visual", theme: TuiToolExecutionTheme.Agent);
        var pending = string.Join('\n', tool.Render(40));
        tool.UpdateResult(new TuiToolExecutionResult([new TuiToolTextBlock("done")]));
        var complete = string.Join('\n', tool.Render(40));
        var failedTool = new TuiToolExecution("write_file", "call-error", theme: TuiToolExecutionTheme.Agent);
        failedTool.UpdateResult(new TuiToolExecutionResult(
            [new TuiToolTextBlock("failed")],
            IsError: true));
        var failed = string.Join('\n', failedTool.Render(40));

        var bash = new TuiBashExecution("dotnet test", theme: TuiBashExecutionTheme.Agent);
        var running = string.Join('\n', bash.Render(40));
        bash.SetComplete(exitCode: 0);
        var done = string.Join('\n', bash.Render(40));
        var failedBash = new TuiBashExecution("dotnet test", theme: TuiBashExecutionTheme.Agent);
        failedBash.SetComplete(exitCode: 1);
        var bashError = string.Join('\n', failedBash.Render(40));

        Assert.Contains("⏳", pending, StringComparison.Ordinal);
        Assert.Contains("✓", complete, StringComparison.Ordinal);
        Assert.Contains("✗", failed, StringComparison.Ordinal);
        Assert.Contains("╭", pending, StringComparison.Ordinal);
        Assert.Contains("╰", pending, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[38;5;252;48;5;236m", pending, StringComparison.Ordinal);
        Assert.Contains("╭", complete, StringComparison.Ordinal);
        Assert.Contains("╭", failed, StringComparison.Ordinal);
        Assert.Contains("⏳", running, StringComparison.Ordinal);
        Assert.Contains("✓", done, StringComparison.Ordinal);
        Assert.Contains("✗", bashError, StringComparison.Ordinal);
        Assert.All(tool.Render(40), line => Assert.Equal(40, TuiText.VisibleWidth(line)));
        Assert.All(failedTool.Render(40), line => Assert.Equal(40, TuiText.VisibleWidth(line)));
        Assert.All(bash.Render(40), line => Assert.Equal(40, TuiText.VisibleWidth(line)));
        Assert.All(failedBash.Render(40), line => Assert.Equal(40, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void MessageArea_AgentThemeUsesSpectrePanelForReasoning()
    {
        var lines = TuiMessageArea.RenderMessages(
            [new TuiMessage(TuiMessageRole.Thinking, "Inspecting the repository")],
            width: 48,
            displayOptions: TuiMessageDisplayOptions.Agent);

        Assert.Contains(lines, line => line.Contains("思维链", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Inspecting the repository", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("╭", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("╰", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Equal(48, TuiText.VisibleWidth(line)));
    }
}
