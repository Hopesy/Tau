using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentSkillStoreTests
{
    [Fact]
    public async Task Load_ReadsProjectSkillFrontmatter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-skills-store-" + Guid.NewGuid().ToString("N"));
        var skillDirectory = Path.Combine(directory, ".tau", "skills", "reviewer");
        Directory.CreateDirectory(skillDirectory);
        var file = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(
            file,
            """
            ---
            name: reviewer
            description: Review source changes
            disable-model-invocation: true
            ---
            Check the diff.
            """);

        try
        {
            var store = new CodingAgentSkillStore(cwd: directory);
            var skill = Assert.Single(store.Load());

            Assert.Equal("reviewer", skill.Name);
            Assert.Equal("Review source changes", skill.Description);
            Assert.True(skill.DisableModelInvocation);
            Assert.Equal(file, skill.FilePath);
            Assert.Equal(skillDirectory, skill.BaseDirectory);
            Assert.Equal("project", skill.Scope);
            Assert.Contains("Check the diff.", skill.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryExpand_WrapsSkillContentAndAppendsArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-skills-expand-" + Guid.NewGuid().ToString("N"));
        var skillDirectory = Path.Combine(directory, ".tau", "skills", "patcher");
        Directory.CreateDirectory(skillDirectory);
        var file = Path.Combine(skillDirectory, "SKILL.md");
        File.WriteAllText(
            file,
            """
            ---
            name: patcher
            description: Patch source files
            ---
            Apply a minimal patch.
            """);

        try
        {
            var store = new CodingAgentSkillStore(cwd: directory);

            var expanded = store.TryExpand("/skill:patcher src/app.cs", out var text, out var skill);

            Assert.True(expanded);
            Assert.NotNull(skill);
            Assert.Contains($"""<skill name="patcher" location="{file}">""", text, StringComparison.Ordinal);
            Assert.Contains($"References are relative to {skillDirectory}.", text, StringComparison.Ordinal);
            Assert.Contains("Apply a minimal patch.", text, StringComparison.Ordinal);
            Assert.EndsWith("src/app.cs", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FormatForSystemPrompt_ExcludesCommandOnlySkills()
    {
        var visible = new CodingAgentSkill("visible", "Visible skill", "body", "visible.md", ".", "path", false);
        var commandOnly = new CodingAgentSkill("command-only", "Hidden skill", "body", "hidden.md", ".", "path", true);

        var prompt = CodingAgentSkillStore.FormatForSystemPrompt([visible, commandOnly]);

        Assert.Contains("<name>visible</name>", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("command-only", prompt, StringComparison.Ordinal);
    }
}
