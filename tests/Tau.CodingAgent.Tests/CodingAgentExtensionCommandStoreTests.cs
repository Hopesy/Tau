using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public class CodingAgentExtensionCommandStoreTests
{
    [Fact]
    public async Task Load_ReadsProjectExtensionCommandsAndResolvesDuplicates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-store-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(directory, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
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
                },
                {
                  "name": "hello",
                  "description": "Say hello again",
                  "response": "Again $1",
                  "sendToRunner": true
                }
              ]
            }
            """);

        try
        {
            var store = new CodingAgentExtensionCommandStore(cwd: directory);
            var commands = store.Load();

            Assert.Equal(2, commands.Count);
            Assert.Collection(
                commands,
                command =>
                {
                    Assert.Equal("hello", command.Name);
                    Assert.Equal("hello:1", command.InvocationName);
                    Assert.Equal("<name>", command.ArgumentHint);
                    Assert.Equal("Say hello", command.Description);
                    Assert.False(command.SendToRunner);
                    Assert.Equal("project", command.Scope);
                },
                command =>
                {
                    Assert.Equal("hello", command.Name);
                    Assert.Equal("hello:2", command.InvocationName);
                    Assert.Equal("Say hello again", command.Description);
                    Assert.True(command.SendToRunner);
                    Assert.Equal("project", command.Scope);
                });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryInvoke_ExpandsStatusOnlyCommand()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-status-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(directory, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        File.WriteAllText(
            Path.Combine(extensions, "hello.json"),
            """
            {
              "name": "hello",
              "description": "Say hello",
              "response": "Hello $1 and ${@:2}"
            }
            """);

        try
        {
            var store = new CodingAgentExtensionCommandStore(cwd: directory);

            var handled = store.TryInvoke("/hello \"Ada Lovelace\" Grace Hopper", out var invocation);

            Assert.True(handled);
            Assert.NotNull(invocation);
            Assert.False(invocation.IsError);
            Assert.False(invocation.SendToRunner);
            Assert.Equal("Hello Ada Lovelace and Grace Hopper", invocation.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryInvoke_ExpandsRunnerCommand()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-runner-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(directory, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        File.WriteAllText(
            Path.Combine(extensions, "review.json"),
            """
            {
              "name": "review",
              "description": "Review a file",
              "prompt": "Review $1 with $ARGUMENTS",
              "sendToRunner": true
            }
            """);

        try
        {
            var store = new CodingAgentExtensionCommandStore(cwd: directory);

            var handled = store.TryInvoke("/review src/app.cs carefully", out var invocation);

            Assert.True(handled);
            Assert.NotNull(invocation);
            Assert.False(invocation.IsError);
            Assert.True(invocation.SendToRunner);
            Assert.Equal("Review src/app.cs with src/app.cs carefully", invocation.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadResources_ReadsPromptAndSkillPathsRelativeToExtensionFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-resources-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "bundle");
        Directory.CreateDirectory(extensionDirectory);
        File.WriteAllText(
            Path.Combine(extensionDirectory, "bundle.json"),
            """
            {
              "name": "noop",
              "response": "ok",
              "resources": {
                "promptPaths": ["./prompts"],
                "skillPaths": ["./skills"],
                "themePaths": ["./themes"]
              }
            }
            """);

        try
        {
            var store = new CodingAgentExtensionCommandStore(cwd: directory);

            var resources = store.LoadResources();

            Assert.Equal(Path.Combine(extensionDirectory, "prompts"), Assert.Single(resources.PromptPaths));
            Assert.Equal(Path.Combine(extensionDirectory, "skills"), Assert.Single(resources.SkillPaths));
            Assert.Equal(Path.Combine(extensionDirectory, "themes"), Assert.Single(resources.ThemePaths));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_ReportsTypescriptAndJavascriptModuleEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-modules-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(directory, ".tau", "extensions");
        Directory.CreateDirectory(extensions);
        Directory.CreateDirectory(Path.Combine(extensions, "nested"));
        Directory.CreateDirectory(Path.Combine(extensions, "packaged", "src"));
        Directory.CreateDirectory(Path.Combine(extensions, "deep", "ignored"));
        File.WriteAllText(Path.Combine(extensions, "direct.ts"), "export default () => {};");
        File.WriteAllText(Path.Combine(extensions, "nested", "index.js"), "export default () => {};");
        File.WriteAllText(Path.Combine(extensions, "deep", "ignored", "not-discovered.ts"), "export default () => {};");
        File.WriteAllText(
            Path.Combine(extensions, "packaged", "package.json"),
            """
            {
              "pi": {
                "extensions": ["src/main.ts"]
              }
            }
            """);
        File.WriteAllText(Path.Combine(extensions, "packaged", "src", "main.ts"), "export default () => {};");

        try
        {
            var store = new CodingAgentExtensionCommandStore(
                cwd: directory,
                userExtensionsDirectory: Path.Combine(directory, "missing-user-extensions"));

            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            Assert.Equal(
                [
                    Path.Combine(extensions, "direct.ts"),
                    Path.Combine(extensions, "nested", "index.js"),
                    Path.Combine(extensions, "packaged", "src", "main.ts")
                ],
                status.Modules.Select(static module => module.FilePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            Assert.All(status.Modules, static module => Assert.Equal("project", module.Scope));
            Assert.Contains(status.Modules, static module =>
                module.Runtime == "typescript" && module.Status == "discovered; runtime pending");
            Assert.Contains(status.Modules, static module =>
                module.Runtime == "javascript" && module.Status == "discovered; runtime pending");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_ReportsFilesResourcesDuplicatesAndDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-status-details-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions");
        Directory.CreateDirectory(extensionDirectory);
        var extensionFile = Path.Combine(extensionDirectory, "commands.json");
        var badFile = Path.Combine(extensionDirectory, "bad.json");
        File.WriteAllText(
            extensionFile,
            """
            {
              "commands": [
                {
                  "name": "hello",
                  "description": "Say hello",
                  "response": "Hello"
                },
                {
                  "name": "hello",
                  "description": "Say hello again",
                  "prompt": "Again",
                  "sendToRunner": true
                }
              ],
              "resources": {
                "promptPaths": ["./prompts"],
                "skillPaths": ["./skills"],
                "themePaths": ["./themes"]
              }
            }
            """);
        File.WriteAllText(badFile, "{ invalid");

        try
        {
            var store = new CodingAgentExtensionCommandStore(cwd: directory);

            var status = store.LoadStatus();

            Assert.Equal(2, status.Commands.Count);
            Assert.Equal(["hello:1", "hello:2"], status.Commands.Select(static command => command.InvocationName).ToArray());

            var file = Assert.Single(status.Files);
            Assert.Equal(extensionFile, file.FilePath);
            Assert.Equal("project", file.Scope);
            Assert.Equal(2, file.CommandCount);
            Assert.Equal(Path.Combine(extensionDirectory, "prompts"), Assert.Single(file.PromptPaths));
            Assert.Equal(Path.Combine(extensionDirectory, "skills"), Assert.Single(file.SkillPaths));
            Assert.Equal(Path.Combine(extensionDirectory, "themes"), Assert.Single(file.ThemePaths));
            Assert.Equal(file.PromptPaths, status.Resources.PromptPaths);
            Assert.Equal(file.SkillPaths, status.Resources.SkillPaths);
            Assert.Equal(file.ThemePaths, status.Resources.ThemePaths);

            var diagnostic = Assert.Single(status.Diagnostics);
            Assert.Equal("error", diagnostic.Severity);
            Assert.Equal(badFile, diagnostic.Path);
            Assert.Equal("project", diagnostic.Scope);
            Assert.Contains("failed to load extension json:", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
