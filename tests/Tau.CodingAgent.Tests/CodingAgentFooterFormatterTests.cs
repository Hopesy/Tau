using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentFooterFormatterTests
{
    [Fact]
    public void FormatLeft_IncludesBranchAndSortedSanitizedExtensionStatuses()
    {
        var directory = CreateGitDirectory("feature/footer");
        var provider = new CodingAgentFooterDataProvider(directory);
        provider.SetExtensionStatus("lint", "queued\nnow");
        provider.SetExtensionStatus("build", "done\tok");
        provider.SetExtensionStatus("empty", " \r\n ");

        try
        {
            var formatted = CodingAgentFooterFormatter.FormatLeft("session: demo", provider);

            Assert.Equal("session: demo (feature/footer) | done ok queued now", formatted);
        }
        finally
        {
            provider.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FormatLeft_FallsBackToReadyWhenBaseStatusIsBlank()
    {
        var formatted = CodingAgentFooterFormatter.FormatLeft(" \t ", footerDataProvider: null);

        Assert.Equal("ready", formatted);
    }

    [Fact]
    public void FormatCwdForFooter_ReplacesHomeWithTilde()
    {
        var home = Path.Combine(Path.GetTempPath(), $"tau-footer-home-{Guid.NewGuid():N}");
        var cwd = Path.Combine(home, "repo", "src");

        var formatted = CodingAgentFooterFormatter.FormatCwdForFooter(cwd, home);

        Assert.Equal($"~{Path.DirectorySeparatorChar}repo{Path.DirectorySeparatorChar}src", formatted);
    }

    [Fact]
    public void FormatCwdForFooter_UsesTildeForHomeItself()
    {
        var home = Path.Combine(Path.GetTempPath(), $"tau-footer-home-{Guid.NewGuid():N}");

        var formatted = CodingAgentFooterFormatter.FormatCwdForFooter(home, home);

        Assert.Equal("~", formatted);
    }

    [Fact]
    public void FormatCwdForFooter_KeepsPathOutsideHome()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tau-footer-root-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var cwd = Path.Combine(root, "workspace");

        var formatted = CodingAgentFooterFormatter.FormatCwdForFooter(cwd, home);

        Assert.Equal(cwd, formatted);
    }

    [Fact]
    public void FormatDefaultLeft_RendersCwdBranchSessionNameAndExtensionStatuses()
    {
        var home = Path.Combine(Path.GetTempPath(), $"tau-footer-home-{Guid.NewGuid():N}");
        var directory = Path.Combine(home, "work", "repo");
        var provider = new CodingAgentFooterDataProvider(CreateGitDirectory("feature/footer", directory));
        provider.SetExtensionStatus("build", "done\tok");

        try
        {
            var formatted = CodingAgentFooterFormatter.FormatDefaultLeft(
                directory,
                home,
                "demo\nsession",
                provider);

            Assert.Equal(
                $"~{Path.DirectorySeparatorChar}work{Path.DirectorySeparatorChar}repo (feature/footer) • demo session | done ok",
                formatted);
        }
        finally
        {
            provider.Dispose();
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void FormatRight_UsesModelOnlyWhenSingleProviderIsAvailable()
    {
        var directory = CreateGitDirectory("main");
        var provider = new CodingAgentFooterDataProvider(directory);
        provider.SetAvailableProviderCount(1);

        try
        {
            var formatted = CodingAgentFooterFormatter.FormatRight(Model(), ThinkingLevel.High, provider);

            Assert.Equal("gpt-5.4 (thinking high)", formatted);
        }
        finally
        {
            provider.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FormatRight_PrependsProviderWhenMultipleProvidersAreAvailable()
    {
        var directory = CreateGitDirectory("main");
        var provider = new CodingAgentFooterDataProvider(directory);
        provider.SetAvailableProviderCount(2);

        try
        {
            var formatted = CodingAgentFooterFormatter.FormatRight(Model(), thinkingLevel: null, provider);

            Assert.Equal("(openai) gpt-5.4 (thinking off)", formatted);
        }
        finally
        {
            provider.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FormatRight_PrependsCompactUsageCostAndContextStats()
    {
        var directory = CreateGitDirectory("main");
        var provider = new CodingAgentFooterDataProvider(directory);
        provider.SetAvailableProviderCount(2);
        var stats = new CodingAgentSessionStats(
            "openai",
            "gpt-5.4",
            TotalMessages: 2,
            UserMessages: 1,
            AssistantMessages: 1,
            ToolResultMessages: 0,
            ToolCalls: 0,
            EstimatedTokens: 512,
            ContextWindowTokens: 128_000,
            SessionName: "footer slice",
            SessionFile: null)
        {
            Tokens = new CodingAgentSessionUsageTotals(1_500, 250_000, 300, 4_000),
            Cost = 0.037m,
            CostRecords = 1
        };

        try
        {
            var formatted = CodingAgentFooterFormatter.FormatRight(
                Model(),
                ThinkingLevel.High,
                provider,
                stats,
                autoCompactEnabled: true);

            Assert.Equal("in1.5k out250k R300 W4.0k $0.037 0.4%/128k(auto) (openai) gpt-5.4 (thinking high)", formatted);
        }
        finally
        {
            provider.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FormatDefaultLines_RendersLocationStatsModelAndExtensionStatuses()
    {
        var home = Path.Combine(Path.GetTempPath(), $"tau-footer-home-{Guid.NewGuid():N}");
        var directory = Path.Combine(home, "work", "repo");
        var provider = new CodingAgentFooterDataProvider(CreateGitDirectory("feature/footer", directory));
        provider.SetAvailableProviderCount(2);
        provider.SetExtensionStatus("build", "done\tok");
        var stats = new CodingAgentSessionStats(
            "openai",
            "gpt-5.4",
            TotalMessages: 2,
            UserMessages: 1,
            AssistantMessages: 1,
            ToolResultMessages: 0,
            ToolCalls: 0,
            EstimatedTokens: 512,
            ContextWindowTokens: 128_000,
            SessionName: "footer slice",
            SessionFile: null)
        {
            Tokens = new CodingAgentSessionUsageTotals(100, 20, 3, 4),
            Cost = 0.037m,
            CostRecords = 1
        };

        try
        {
            var lines = CodingAgentFooterFormatter.FormatDefaultLines(
                directory,
                home,
                "demo\nsession",
                Model(),
                ThinkingLevel.High,
                provider,
                stats);

            Assert.Equal(3, lines.Count);
            Assert.Equal(
                $"~{Path.DirectorySeparatorChar}work{Path.DirectorySeparatorChar}repo (feature/footer) • demo session",
                lines[0].Left);
            Assert.Equal(string.Empty, lines[0].Right);
            Assert.Equal("in100 out20 R3 W4 $0.037 0.4%/128k(auto)", lines[1].Left);
            Assert.Equal("(openai) gpt-5.4 (thinking high)", lines[1].Right);
            Assert.Equal("done ok", lines[2].Left);
            Assert.Equal(string.Empty, lines[2].Right);
        }
        finally
        {
            provider.Dispose();
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void SanitizeStatusText_CollapsesControlAndWhitespace()
    {
        var formatted = CodingAgentFooterFormatter.SanitizeStatusText(" alpha\r\n beta\t\u0001 gamma  ");

        Assert.Equal("alpha beta gamma", formatted);
    }

    private static Model Model() => new()
    {
        Provider = "openai",
        Id = "gpt-5.4",
        Name = "GPT-5.4",
        Api = "openai-responses",
        Reasoning = true
    };

    private static string CreateGitDirectory(string branch, string? directory = null)
    {
        directory ??= Path.Combine(Path.GetTempPath(), $"tau-footer-formatter-{Guid.NewGuid():N}");
        var gitDirectory = Path.Combine(directory, ".git");
        Directory.CreateDirectory(gitDirectory);
        File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), $"ref: refs/heads/{branch}");
        return directory;
    }
}
