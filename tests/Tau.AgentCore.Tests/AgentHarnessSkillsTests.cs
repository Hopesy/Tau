using Tau.AgentCore.Harness;

namespace Tau.AgentCore.Tests;

public sealed class AgentHarnessSkillsTests
{
    [Fact]
    public void LoadSkills_LoadsRootSkillAndStopsDescending()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "SKILL.md"),
            """
            ---
            name: root-skill
            description: Root skill
            ---
            Root body
            """);
        Directory.CreateDirectory(Path.Combine(temp.Path, "nested"));
        File.WriteAllText(
            Path.Combine(temp.Path, "nested", "SKILL.md"),
            """
            ---
            name: nested
            description: Nested skill
            ---
            Nested body
            """);

        var result = AgentHarnessSkills.LoadSkills(temp.Path);

        var skill = Assert.Single(result.Skills);
        Assert.Equal("root-skill", skill.Name);
        Assert.Equal("Root skill", skill.Description);
        Assert.Equal("Root body", skill.Content);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "invalid_metadata" &&
                diagnostic.Message.Contains("does not match parent directory", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSkills_RecursesSkillDirectoriesAndLoadsRootMarkdownFiles()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "standalone.md"),
            """
            ---
            name: standalone
            description: Standalone skill
            ---
            Standalone body
            """);
        var nested = Path.Combine(temp.Path, "nested-skill");
        Directory.CreateDirectory(nested);
        File.WriteAllText(
            Path.Combine(nested, "SKILL.md"),
            """
            ---
            description: Nested skill
            ---
            Nested body
            """);
        var deep = Path.Combine(nested, "deeper");
        Directory.CreateDirectory(deep);
        File.WriteAllText(
            Path.Combine(deep, "extra.md"),
            """
            ---
            name: should-not-load
            description: Should not load
            ---
            Hidden
            """);

        var result = AgentHarnessSkills.LoadSkills(temp.Path);

        Assert.Equal(["nested-skill", "standalone"], result.Skills.Select(static skill => skill.Name).Order());
        Assert.DoesNotContain(result.Skills, static skill => skill.Name == "should-not-load");
        Assert.Equal("Nested body", result.Skills.Single(static skill => skill.Name == "nested-skill").Content);
    }

    [Fact]
    public void LoadSkills_HonorsIgnoreFilesWhileRecursing()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(Path.Combine(temp.Path, ".gitignore"), "ignored-skill/\n");
        var ignored = Path.Combine(temp.Path, "ignored-skill");
        Directory.CreateDirectory(ignored);
        File.WriteAllText(
            Path.Combine(ignored, "SKILL.md"),
            """
            ---
            description: Ignored skill
            ---
            Ignored body
            """);
        var visible = Path.Combine(temp.Path, "visible-skill");
        Directory.CreateDirectory(visible);
        File.WriteAllText(
            Path.Combine(visible, "SKILL.md"),
            """
            ---
            description: Visible skill
            ---
            Visible body
            """);

        var result = AgentHarnessSkills.LoadSkills(temp.Path);

        var skill = Assert.Single(result.Skills);
        Assert.Equal("visible-skill", skill.Name);
    }

    [Fact]
    public void LoadSkills_ReturnsMetadataDiagnosticsAndSkipsMissingDescription()
    {
        using var temp = TempDirectory.Create();
        var invalid = Path.Combine(temp.Path, "Invalid_Name");
        Directory.CreateDirectory(invalid);
        File.WriteAllText(
            Path.Combine(invalid, "SKILL.md"),
            """
            ---
            name: Invalid_Name
            ---
            body
            """);

        var result = AgentHarnessSkills.LoadSkills(temp.Path);

        Assert.Empty(result.Skills);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "invalid_metadata" &&
            diagnostic.Message == "description is required");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "invalid_metadata" &&
            diagnostic.Message.Contains("invalid characters", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadSourcedSkills_PreservesSourceValues()
    {
        using var temp = TempDirectory.Create();
        var skillDir = Path.Combine(temp.Path, "review");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            description: Review skill
            ---
            Review body
            """);

        var result = AgentHarnessSkills.LoadSourcedSkills([(temp.Path, "extension")]);

        var sourced = Assert.Single(result.Skills);
        Assert.Equal("extension", sourced.Source);
        Assert.Equal("review", sourced.Skill.Name);
    }

    [Fact]
    public void FormatSkillsForSystemPrompt_ExcludesDisabledSkillsAndEscapesXml()
    {
        var visible = new AgentHarnessSkill(
            "review",
            "Use <review> & \"quote\" 'apostrophe'",
            "body",
            "C:/skills/review/SKILL.md");
        var hidden = new AgentHarnessSkill(
            "hidden",
            "Hidden",
            "body",
            "C:/skills/hidden/SKILL.md",
            DisableModelInvocation: true);

        var prompt = AgentHarnessSkills.FormatSkillsForSystemPrompt([visible, hidden]);

        Assert.Contains("The following skills provide specialized instructions for specific tasks.", prompt, StringComparison.Ordinal);
        Assert.Contains("<name>review</name>", prompt, StringComparison.Ordinal);
        Assert.Contains("Use &lt;review&gt; &amp; &quot;quote&quot; &apos;apostrophe&apos;", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSkillInvocation_WrapsSkillContentAndAdditionalInstructions()
    {
        var skill = new AgentHarnessSkill(
            "review",
            "Review code",
            "Read references/file.md",
            Path.Combine("C:", "skills", "review", "SKILL.md"));

        var invocation = AgentHarnessSkills.FormatSkillInvocation(skill, "Focus tests.");

        Assert.Contains("""<skill name="review" location=""", invocation, StringComparison.Ordinal);
        Assert.Contains("References are relative to", invocation, StringComparison.Ordinal);
        Assert.Contains("Read references/file.md", invocation, StringComparison.Ordinal);
        Assert.EndsWith("Focus tests.", invocation, StringComparison.Ordinal);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tau-agentcore-skills-" + Guid.NewGuid().ToString("N"));
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
