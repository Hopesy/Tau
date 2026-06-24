using Tau.AgentCore.Harness;

namespace Tau.AgentCore.Tests;

public sealed class AgentHarnessPromptTemplatesTests
{
    [Fact]
    public void LoadPromptTemplates_LoadsDirectMarkdownChildrenAndSkipsNestedFiles()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "review.md"),
            """
            ---
            description: Review the current diff
            argument-hint: files
            ---
            Review $ARGUMENTS
            """);
        File.WriteAllText(Path.Combine(temp.Path, "ignore.txt"), "ignore");
        Directory.CreateDirectory(Path.Combine(temp.Path, "nested"));
        File.WriteAllText(Path.Combine(temp.Path, "nested", "nested.md"), "Nested");

        var result = AgentHarnessPromptTemplates.LoadPromptTemplates(temp.Path);

        var template = Assert.Single(result.PromptTemplates);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("review", template.Name);
        Assert.Equal("Review the current diff", template.Description);
        Assert.Equal("Review $ARGUMENTS", template.Content);
    }

    [Fact]
    public void LoadPromptTemplates_UsesFirstBodyLineWhenDescriptionIsMissing()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "summarize.md");
        File.WriteAllText(filePath, "Summarize this file\n\nMore text");

        var result = AgentHarnessPromptTemplates.LoadPromptTemplates(filePath);

        var template = Assert.Single(result.PromptTemplates);
        Assert.Equal("summarize", template.Name);
        Assert.Equal("Summarize this file", template.Description);
        Assert.Equal("Summarize this file\n\nMore text", template.Content);
    }

    [Fact]
    public void LoadPromptTemplates_ReturnsParseDiagnosticForInvalidFrontmatter()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "bad.md");
        File.WriteAllText(
            filePath,
            """
            ---
            description: ok
            invalid
            ---
            body
            """);

        var result = AgentHarnessPromptTemplates.LoadPromptTemplates(filePath);

        Assert.Empty(result.PromptTemplates);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("warning", diagnostic.Type);
        Assert.Equal("parse_failed", diagnostic.Code);
        Assert.Equal(filePath, diagnostic.Path);
    }

    [Fact]
    public void LoadSourcedPromptTemplates_PreservesSourceValues()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "do.md");
        File.WriteAllText(filePath, "Do it");

        var result = AgentHarnessPromptTemplates.LoadSourcedPromptTemplates([(filePath, "extension")]);

        var sourced = Assert.Single(result.PromptTemplates);
        Assert.Equal("extension", sourced.Source);
        Assert.Equal("do", sourced.PromptTemplate.Name);
    }

    [Fact]
    public void ParseCommandArgsAndSubstituteArgs_MatchesUpstreamPlaceholders()
    {
        var args = AgentHarnessPromptTemplates.ParseCommandArgs("""one "two words" 'three words'""");

        Assert.Equal(["one", "two words", "three words"], args);
        Assert.Equal(
            "one|two words|one two words three words|two words three words|two words",
            AgentHarnessPromptTemplates.SubstituteArgs("$1|$2|$@|${@:2}|${@:2:1}", args));
    }

    [Fact]
    public void FormatPromptTemplateInvocation_SubstitutesArguments()
    {
        var template = new AgentPromptTemplate("review", "desc", "Review $1 with $ARGUMENTS");

        Assert.Equal(
            "Review README.md with README.md carefully",
            AgentHarnessPromptTemplates.FormatPromptTemplateInvocation(template, ["README.md", "carefully"]));
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tau-agentcore-prompts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
