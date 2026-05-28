using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentAutocompleteProviderFactoryTests
{
    [Fact]
    public async Task Create_IncludesLocalPromptSkillExtensionAndCatalogCommands()
    {
        using var temp = TempDirectory.Create();
        var prompts = Path.Combine(temp.Path, ".tau", "prompts");
        var skills = Path.Combine(temp.Path, ".tau", "skills", "reviewer");
        var extensions = Path.Combine(temp.Path, ".tau", "extensions");
        Directory.CreateDirectory(prompts);
        Directory.CreateDirectory(skills);
        Directory.CreateDirectory(extensions);

        await File.WriteAllTextAsync(
            Path.Combine(prompts, "review.md"),
            """
            ---
            description: Review prompt
            argument-hint: <file>
            ---
            Review $1.
            """);
        await File.WriteAllTextAsync(
            Path.Combine(skills, "SKILL.md"),
            """
            ---
            name: reviewer
            description: Review skill
            ---
            Check the diff.
            """);
        await File.WriteAllTextAsync(
            Path.Combine(extensions, "commands.json"),
            """
            {
              "commands": [
                {
                  "name": "hello",
                  "description": "Say hello",
                  "argumentHint": "<name>",
                  "response": "Hello $ARGUMENTS"
                }
              ]
            }
            """);

        var provider = CodingAgentAutocompleteProviderFactory.Create(
            new CodingAgentPromptTemplateStore(cwd: temp.Path),
            new CodingAgentSkillStore(cwd: temp.Path),
            new CodingAgentExtensionCommandStore(cwd: temp.Path),
            basePath: temp.Path);

        var modelSuggestions = await provider.GetSuggestionsAsync("/mod", 4);
        Assert.NotNull(modelSuggestions);
        var model = Assert.Single(modelSuggestions!.Items, item => item.Value == "model");
        Assert.Contains("Show, interactively select", model.Description, StringComparison.Ordinal);
        Assert.Contains("provider/model", model.Description, StringComparison.Ordinal);

        var promptSuggestions = await provider.GetSuggestionsAsync("/rev", 4);
        Assert.NotNull(promptSuggestions);
        var prompt = Assert.Single(promptSuggestions!.Items, item => item.Value == "review");
        Assert.Equal("<file> - Review prompt", prompt.Description);

        var skillSuggestions = await provider.GetSuggestionsAsync("/skill", 6);
        Assert.NotNull(skillSuggestions);
        Assert.Contains(skillSuggestions!.Items, item => item.Value == "skill:reviewer");

        var extensionSuggestions = await provider.GetSuggestionsAsync("/hel", 4);
        Assert.NotNull(extensionSuggestions);
        var extension = Assert.Single(extensionSuggestions!.Items, item => item.Value == "hello");
        Assert.Equal("<name> - Say hello", extension.Description);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "tau-coding-agent-autocomplete-" + Guid.NewGuid().ToString("N"));

        private TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public static TempDirectory Create() => new();

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
