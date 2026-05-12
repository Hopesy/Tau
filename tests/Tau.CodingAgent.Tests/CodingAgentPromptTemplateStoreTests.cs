using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentPromptTemplateStoreTests
{
    [Fact]
    public async Task Load_ReadsProjectPromptFrontmatter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-prompts-store-" + Guid.NewGuid().ToString("N"));
        var prompts = Path.Combine(directory, ".tau", "prompts");
        Directory.CreateDirectory(prompts);
        var file = Path.Combine(prompts, "review.md");
        await File.WriteAllTextAsync(
            file,
            """
            ---
            description: Review target
            argument-hint: <path>
            ---
            Review $1.
            """);

        try
        {
            var store = new CodingAgentPromptTemplateStore(cwd: directory);
            var template = Assert.Single(store.Load());

            Assert.Equal("review", template.Name);
            Assert.Equal("Review target", template.Description);
            Assert.Equal("<path>", template.ArgumentHint);
            Assert.Equal(file, template.FilePath);
            Assert.Equal("project", template.Scope);
            Assert.Contains("Review $1.", template.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryExpand_AppliesPositionalAndSlicedArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-prompts-expand-" + Guid.NewGuid().ToString("N"));
        var prompts = Path.Combine(directory, ".tau", "prompts");
        Directory.CreateDirectory(prompts);
        File.WriteAllText(
            Path.Combine(prompts, "patch.md"),
            "File $1\nAll $ARGUMENTS\nRest ${@:2}\nOne ${@:2:1}\nAt $@");

        try
        {
            var store = new CodingAgentPromptTemplateStore(cwd: directory);

            var expanded = store.TryExpand("/patch \"src/app.cs\" fix now", out var text, out var template);

            Assert.True(expanded);
            Assert.NotNull(template);
            Assert.Contains("File src/app.cs", text, StringComparison.Ordinal);
            Assert.Contains("All src/app.cs fix now", text, StringComparison.Ordinal);
            Assert.Contains("Rest fix now", text, StringComparison.Ordinal);
            Assert.Contains("One fix", text, StringComparison.Ordinal);
            Assert.Contains("At src/app.cs fix now", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
