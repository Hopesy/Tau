using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiMarkdownTests
{
    [Fact]
    public void Markdown_RendersHeadingsParagraphAndInlineStyles()
    {
        var theme = new TuiMarkdownTheme
        {
            Heading = static value => $"H[{value}]",
            Bold = static value => $"B[{value}]",
            Italic = static value => $"I[{value}]",
            Strikethrough = static value => $"S[{value}]",
            Underline = static value => $"U[{value}]",
            Code = static value => $"`{value}`",
        };
        var markdown = new TuiMarkdown(
            "# Title\n\nHello **bold** and *em* and `code` and ~~gone~~",
            theme: theme);

        var lines = markdown.Render(100);

        Assert.Equal("H[B[U[Title]]]", lines[0].TrimEnd());
        Assert.Equal(string.Empty, lines[1].TrimEnd());
        Assert.Contains("Hello B[bold] and I[em] and `code` and S[gone]", lines[2], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.True(TuiText.VisibleWidth(line) <= 100));
    }

    [Fact]
    public void Markdown_RendersLinkFallbackOrOsc8Hyperlink()
    {
        var fallback = new TuiMarkdown("See [Docs](https://example.com) and [mailto:user@example.com](mailto:user@example.com)");

        var fallbackLine = fallback.Render(100)[0];

        Assert.Contains("Docs (https://example.com)", fallbackLine, StringComparison.Ordinal);
        Assert.Contains("mailto:user@example.com", fallbackLine, StringComparison.Ordinal);
        Assert.DoesNotContain("(mailto:user@example.com)", fallbackLine, StringComparison.Ordinal);

        var hyperlink = new TuiMarkdown("[Docs](https://example.com)", enableHyperlinks: true);

        var hyperlinkLine = hyperlink.Render(100)[0];

        Assert.Contains("\u001b]8;;https://example.com\u001b\\Docs\u001b]8;;\u001b\\", hyperlinkLine, StringComparison.Ordinal);
        Assert.DoesNotContain("(https://example.com)", hyperlinkLine, StringComparison.Ordinal);
        Assert.Equal(100, TuiText.VisibleWidth(hyperlinkLine));
    }

    [Fact]
    public void Markdown_RendersFencedCodeWithThemeIndentAndHighlightHook()
    {
        var theme = new TuiMarkdownTheme
        {
            CodeBlockBorder = static value => $"border:{value}",
            CodeBlockIndent = ">>",
            HighlightCode = static (code, lang) => [$"{lang}:{code.ToUpperInvariant()}"],
        };
        var markdown = new TuiMarkdown("```cs\nvar x = 1;\n```", theme: theme);

        var lines = markdown.Render(80);

        Assert.Equal("border:```cs", lines[0].TrimEnd());
        Assert.Equal(">>cs:VAR X = 1;", lines[1].TrimEnd());
        Assert.Equal("border:```", lines[2].TrimEnd());
    }

    [Fact]
    public void Markdown_RendersUnorderedOrderedAndNestedLists()
    {
        var markdown = new TuiMarkdown("- one\n  - two\n3. three");

        var lines = markdown.Render(40);

        Assert.Equal("- one", lines[0].TrimEnd());
        Assert.Equal("  - two", lines[1].TrimEnd());
        Assert.Equal("3. three", lines[2].TrimEnd());
    }

    [Fact]
    public void Markdown_RendersWidthAwareTables()
    {
        var markdown = new TuiMarkdown("""
            | Name | Value |
            | --- | --- |
            | alpha | beta |
            | longer-name | wrapped value |
            """);

        var lines = markdown.Render(32);

        Assert.StartsWith("\u250c", lines[0], StringComparison.Ordinal);
        Assert.Contains("Name", lines[1], StringComparison.Ordinal);
        Assert.Contains("Value", lines[1], StringComparison.Ordinal);
        Assert.Contains("\u251c", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("\u2514", lines[^1], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal(32, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void Markdown_RendersBlockquotesAndHorizontalRules()
    {
        var markdown = new TuiMarkdown("> quoted **text**\n---");

        var lines = markdown.Render(24);

        Assert.StartsWith("\u2502 quoted text", lines[0], StringComparison.Ordinal);
        Assert.StartsWith(new string('\u2500', 24), lines[1], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal(24, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void Markdown_AppliesPaddingBackgroundCacheAndSetText()
    {
        var markdown = new TuiMarkdown(
            "body",
            paddingX: 1,
            paddingY: 1,
            defaultTextStyle: new TuiDefaultTextStyle(
                BackgroundColor: static value => $"\u001b[41m{value}\u001b[0m"));

        var first = markdown.Render(8);
        var second = markdown.Render(8);

        Assert.Same(first, second);
        Assert.Equal(3, first.Count);
        Assert.All(first, line =>
        {
            Assert.StartsWith("\u001b[41m", line, StringComparison.Ordinal);
            Assert.Equal(8, TuiText.VisibleWidth(line));
        });

        markdown.SetText("next");

        var updated = markdown.Render(8);
        Assert.NotSame(first, updated);
        Assert.Contains("next", updated[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_DoesNotWrapOrPadTerminalImageEscapeLines()
    {
        const string imageLine = "\u001b_Ga=T;abc\u001b\\";
        var markdown = new TuiMarkdown(imageLine, paddingX: 2);

        var lines = markdown.Render(4);

        Assert.Equal([imageLine], lines);
    }
}
