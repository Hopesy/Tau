using Tau.Tui.Rendering;

namespace Tau.Tui.Tests;

public sealed class TuiSyntaxHighlighterTests
{
    [Fact]
    public void SupportsLanguage_NormalizesCommonAliases()
    {
        Assert.True(TuiSyntaxHighlighter.SupportsLanguage("ts"));
        Assert.True(TuiSyntaxHighlighter.SupportsLanguage("c#"));
        Assert.True(TuiSyntaxHighlighter.SupportsLanguage("ps1"));
        Assert.True(TuiSyntaxHighlighter.SupportsLanguage("html"));
        Assert.False(TuiSyntaxHighlighter.SupportsLanguage("plain-prose"));
    }

    [Fact]
    public void HighlightLines_ColorsDiffAdditionsAndDeletions()
    {
        var lines = TuiSyntaxHighlighter.HighlightLines("-old\n+new", "diff");

        Assert.Equal("\u001b[38;2;204;102;102m-old\u001b[39m", lines[0]);
        Assert.Equal("\u001b[38;2;181;189;104m+new\u001b[39m", lines[1]);
    }

    [Fact]
    public void HighlightLines_ColorsJavaScriptKeywordsRegexAndNumbers()
    {
        var line = TuiSyntaxHighlighter.HighlightLines("const re = /foo+/gi; count = 1", "javascript")[0];

        Assert.Contains("\u001b[38;2;86;156;214mconst\u001b[39m", line, StringComparison.Ordinal);
        Assert.Contains("\u001b[38;2;206;145;120m/foo+/gi\u001b[39m", line, StringComparison.Ordinal);
        Assert.Contains("\u001b[38;2;181;206;168m1\u001b[39m", line, StringComparison.Ordinal);
    }

    [Fact]
    public void HighlightLines_ColorsPythonDecoratorsAndXmlTags()
    {
        var decorator = TuiSyntaxHighlighter.HighlightLines("@decorator", "python")[0];
        var xml = TuiSyntaxHighlighter.HighlightLines("<div class=\"x\"></div>", "html")[0];

        Assert.Equal("\u001b[38;2;128;128;128m@decorator\u001b[39m", decorator);
        Assert.Contains("\u001b[38;2;86;156;214mdiv\u001b[39m", xml, StringComparison.Ordinal);
        Assert.Contains("\u001b[38;2;156;220;254mclass\u001b[39m", xml, StringComparison.Ordinal);
        Assert.Contains("\u001b[38;2;206;145;120m\"x\"\u001b[39m", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void HighlightLines_DoesNotAutoDetectUnknownLanguages()
    {
        var line = TuiSyntaxHighlighter.HighlightLines("const value = 1", "plain-prose")[0];

        Assert.Equal("\u001b[38;2;212;212;212mconst value = 1\u001b[39m", line);
    }
}
