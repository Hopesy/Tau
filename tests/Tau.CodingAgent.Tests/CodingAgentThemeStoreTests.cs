using Tau.CodingAgent.Runtime;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

public class CodingAgentThemeStoreTests
{
    [Fact]
    public void LoadStatus_LoadsBuiltInProjectExplicitAndExtensionThemesWithLastWriterWins()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-themes-store-" + Guid.NewGuid().ToString("N"));
        var projectThemes = Path.Combine(directory, ".tau", "themes");
        var explicitThemes = Path.Combine(directory, "explicit-themes");
        var extensionThemes = Path.Combine(directory, "extension-themes");
        Directory.CreateDirectory(projectThemes);
        Directory.CreateDirectory(explicitThemes);
        Directory.CreateDirectory(extensionThemes);
        File.WriteAllText(Path.Combine(projectThemes, "dark.json"), CreateThemeJson("dark", "#111111"));
        File.WriteAllText(Path.Combine(projectThemes, "project.json"), CreateThemeJson("project", "#222222"));
        File.WriteAllText(Path.Combine(explicitThemes, "explicit.json"), CreateThemeJson("explicit", "#333333"));
        File.WriteAllText(Path.Combine(extensionThemes, "extension.json"), CreateThemeJson("extension", "#444444"));

        try
        {
            var store = new CodingAgentThemeStore(
                cwd: directory,
                userThemesDirectory: Path.Combine(directory, "missing-user"),
                explicitPaths: [explicitThemes],
                additionalPathsProvider: () => [extensionThemes]);

            var status = store.LoadStatus();

            Assert.Contains(status.Themes, theme => theme.Name == "light" && theme.Scope == "builtin");
            Assert.Contains(status.Themes, theme => theme.Name == "project" && theme.Scope == "project");
            Assert.Contains(status.Themes, theme => theme.Name == "explicit" && theme.Scope == "path");
            Assert.Contains(status.Themes, theme => theme.Name == "extension" && theme.Scope == "path");
            var dark = Assert.Single(status.Themes, theme => theme.Name == "dark");
            Assert.Equal("project", dark.Scope);
            Assert.Equal(Path.Combine(projectThemes, "dark.json"), dark.FilePath);
            Assert.Equal("#111111", dark.Colors["accent"]);
            Assert.Equal("#101010", dark.ExportColors["pageBg"]);
            Assert.Contains(status.Diagnostics, diagnostic =>
                diagnostic.Message.Contains("collision", StringComparison.OrdinalIgnoreCase) &&
                diagnostic.Path == Path.Combine(projectThemes, "dark.json"));
            var collision = Assert.Single(status.ResourceDiagnostics, diagnostic =>
                diagnostic.Type == CodingAgentResourceDiagnosticTypes.Collision &&
                diagnostic.Collision?.Name == "dark");
            Assert.Equal(Path.Combine(projectThemes, "dark.json"), collision.Path);
            Assert.NotNull(collision.Collision);
            Assert.Equal(CodingAgentResourceTypes.Theme, collision.Collision.ResourceType);
            Assert.Equal(Path.Combine(projectThemes, "dark.json"), collision.Collision.WinnerPath);
            Assert.Equal("<builtin>", collision.Collision.LoserPath);
            Assert.Equal("project", collision.Collision.WinnerSource);
            Assert.Equal("builtin", collision.Collision.LoserSource);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ThemeSelector_CreateSelectList_SelectsCurrentThemeAndKeepsLocationDescription()
    {
        var themePath = Path.Combine(Path.GetTempPath(), "tau-theme-selector-" + Guid.NewGuid().ToString("N") + ".json");
        var status = new CodingAgentThemeStatus(
            [
                new CodingAgentTheme("dark", null, "builtin", new Dictionary<string, string>(), new Dictionary<string, string>()),
                new CodingAgentTheme("solarized", themePath, "project", new Dictionary<string, string>(), new Dictionary<string, string>()),
                new CodingAgentTheme("light", null, "builtin", new Dictionary<string, string>(), new Dictionary<string, string>())
            ],
            []);

        var selector = CodingAgentThemeSelector.CreateSelectList(status, "solarized");

        Assert.Equal("solarized", selector.SelectedItem?.Value);
        Assert.Equal(3, selector.FilteredItems.Count);
        var selected = selector.SelectedItem;
        Assert.NotNull(selected);
        Assert.Equal($"project {themePath}", selected.Description);
    }

    [Fact]
    public void LoadStatus_ReportsInvalidJsonMissingColorsAndMissingExplicitPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-themes-invalid-" + Guid.NewGuid().ToString("N"));
        var themes = Path.Combine(directory, "themes");
        Directory.CreateDirectory(themes);
        var badJson = Path.Combine(themes, "bad.json");
        var missingColor = Path.Combine(themes, "missing-color.json");
        var missingPath = Path.Combine(directory, "missing.json");
        File.WriteAllText(badJson, "{ invalid");
        File.WriteAllText(
            missingColor,
            """
            {
              "name": "missing",
              "colors": {}
            }
            """);

        try
        {
            var store = new CodingAgentThemeStore(
                cwd: directory,
                explicitPaths: [themes, missingPath],
                includeDefaults: false);

            var status = store.LoadStatus();

            Assert.Empty(status.Themes);
            Assert.Contains(status.Diagnostics, diagnostic =>
                diagnostic.Severity == "error" &&
                diagnostic.Path == badJson &&
                diagnostic.Message.StartsWith("failed to load theme json:", StringComparison.Ordinal));
            Assert.Contains(status.Diagnostics, diagnostic =>
                diagnostic.Severity == "error" &&
                diagnostic.Path == missingColor &&
                diagnostic.Message == "theme json missing required color 'accent'");
            Assert.Contains(status.Diagnostics, diagnostic =>
                diagnostic.Severity == "warning" &&
                diagnostic.Path == missingPath &&
                diagnostic.Message == "theme path does not exist");
            Assert.Contains(status.ResourceDiagnostics, diagnostic =>
                diagnostic.Type == CodingAgentResourceDiagnosticTypes.Error &&
                diagnostic.Path == badJson &&
                diagnostic.Message.StartsWith("failed to load theme json:", StringComparison.Ordinal));
            Assert.Contains(status.ResourceDiagnostics, diagnostic =>
                diagnostic.Type == CodingAgentResourceDiagnosticTypes.Warning &&
                diagnostic.Path == missingPath &&
                diagnostic.Message == "theme path does not exist");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_ResolvesRecursiveVariablesInColorsAndExportColors()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-themes-vars-" + Guid.NewGuid().ToString("N"));
        var themes = Path.Combine(directory, "themes");
        Directory.CreateDirectory(themes);
        File.WriteAllText(
            Path.Combine(themes, "recursive.json"),
            CreateThemeJson("recursive", "accentAlias")
                .Replace(
                    """
                    "accentVar": "accentAlias"
                    """,
                    """
                    "accentVar": "accentAlias",
                                "pageAlias": "page",
                                "card": "#202020",
                                "cardAlias": "card",
                                "info": "#303030",
                                "infoAlias": "info",
                                "accentLeaf": "#abcdef",
                                "accentAlias": "accentLeaf"
                    """,
                    StringComparison.Ordinal)
                .Replace(
                    """
                    "pageBg": "page"
                    """,
                    """
                    "pageBg": "pageAlias",
                        "cardBg": "cardAlias",
                        "infoBg": "infoAlias"
                    """,
                    StringComparison.Ordinal));

        try
        {
            var store = new CodingAgentThemeStore(
                cwd: directory,
                explicitPaths: [themes],
                includeDefaults: false);

            var theme = Assert.Single(store.LoadStatus().Themes);

            Assert.Equal("#abcdef", theme.Colors["accent"]);
            Assert.Equal("#101010", theme.ExportColors["pageBg"]);
            Assert.Equal("#202020", theme.ExportColors["cardBg"]);
            Assert.Equal("#303030", theme.ExportColors["infoBg"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Theme_ToSyntaxHighlightTheme_UsesConfiguredSyntaxColors()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-themes-syntax-" + Guid.NewGuid().ToString("N"));
        var themes = Path.Combine(directory, "themes");
        Directory.CreateDirectory(themes);
        File.WriteAllText(
            Path.Combine(themes, "syntax.json"),
            CreateThemeJson("syntax")
                .Replace("\"syntaxKeyword\": \"#ff7b72\"", "\"syntaxKeyword\": \"#010203\"", StringComparison.Ordinal)
                .Replace("\"syntaxNumber\": \"#79c0ff\"", "\"syntaxNumber\": 208", StringComparison.Ordinal));

        try
        {
            var store = new CodingAgentThemeStore(
                cwd: directory,
                explicitPaths: [themes],
                includeDefaults: false);

            var theme = Assert.Single(store.LoadStatus().Themes);
            var line = TuiSyntaxHighlighter.HighlightLines("const count = 1", "javascript", theme.ToSyntaxHighlightTheme())[0];
            var diffLines = TuiSyntaxHighlighter.HighlightLines("+new\n-old", "diff", theme.ToSyntaxHighlightTheme());

            Assert.Contains("\u001b[38;2;1;2;3mconst\u001b[39m", line, StringComparison.Ordinal);
            Assert.Contains("\u001b[38;5;208m1\u001b[39m", line, StringComparison.Ordinal);
            Assert.Equal("\u001b[38;2;63;185;80m+new\u001b[39m", diffLines[0]);
            Assert.Equal("\u001b[38;2;248;81;73m-old\u001b[39m", diffLines[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static string CreateThemeJson(string name, string accent = "#8ab4f8") =>
        $$"""
        {
          "name": "{{name}}",
          "vars": {
            "page": "#101010",
            "accentVar": "{{accent}}"
          },
          "colors": {
            "accent": "accentVar",
            "border": "#30363d",
            "borderAccent": "#8ab4f8",
            "borderMuted": "#30363d",
            "success": "#3fb950",
            "error": "#f85149",
            "warning": "#d29922",
            "muted": "#8b949e",
            "dim": "#6e7681",
            "text": "#c9d1d9",
            "thinkingText": "#a5d6ff",
            "selectedBg": "#1f6feb",
            "userMessageBg": "#161b22",
            "userMessageText": "#c9d1d9",
            "customMessageBg": "#161b22",
            "customMessageText": "#c9d1d9",
            "customMessageLabel": "#8ab4f8",
            "toolPendingBg": "#1f2937",
            "toolSuccessBg": "#102a12",
            "toolErrorBg": "#3b1111",
            "toolTitle": "#8ab4f8",
            "toolOutput": "#c9d1d9",
            "mdHeading": "#8ab4f8",
            "mdLink": "#58a6ff",
            "mdLinkUrl": "#8b949e",
            "mdCode": "#f0f6fc",
            "mdCodeBlock": "#c9d1d9",
            "mdCodeBlockBorder": "#30363d",
            "mdQuote": "#8b949e",
            "mdQuoteBorder": "#30363d",
            "mdHr": "#30363d",
            "mdListBullet": "#8ab4f8",
            "toolDiffAdded": "#3fb950",
            "toolDiffRemoved": "#f85149",
            "toolDiffContext": "#8b949e",
            "syntaxComment": "#8b949e",
            "syntaxKeyword": "#ff7b72",
            "syntaxFunction": "#d2a8ff",
            "syntaxVariable": "#ffa657",
            "syntaxString": "#a5d6ff",
            "syntaxNumber": "#79c0ff",
            "syntaxType": "#ffa657",
            "syntaxOperator": "#ff7b72",
            "syntaxPunctuation": "#c9d1d9",
            "thinkingOff": "#6e7681",
            "thinkingMinimal": "#8b949e",
            "thinkingLow": "#58a6ff",
            "thinkingMedium": "#d29922",
            "thinkingHigh": "#f85149",
            "thinkingXhigh": "#d2a8ff",
            "bashMode": "#3fb950"
          },
          "export": {
            "pageBg": "page"
          }
        }
        """;
}
