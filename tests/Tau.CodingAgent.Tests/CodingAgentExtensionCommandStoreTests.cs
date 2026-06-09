using System.Diagnostics;
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
        File.WriteAllText(Path.Combine(extensions, "nested", "index.js"), "module.exports = () => {};");
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
                module.Runtime == "javascript" && module.Status == "loaded; commands 0; limited runtime");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_LoadsJavascriptRegisteredCommands()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-load-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "hello");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerCommand("hello", {
                description: "Say hello",
                argumentHint: "<name>",
                handler: async (args, ctx) => {
                  ctx.sendMessage(`Hello ${args}`);
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            var command = Assert.Single(status.Commands);
            Assert.Equal("hello", command.Name);
            Assert.Equal("hello", command.InvocationName);
            Assert.Equal("Say hello", command.Description);
            Assert.Equal("<name>", command.ArgumentHint);
            Assert.Equal("project", command.Scope);
            Assert.Equal("javascript", command.Runtime);
            Assert.False(command.SendToRunner);

            var module = Assert.Single(status.Modules);
            Assert.Equal(Path.Combine(extensionDirectory, "index.js"), module.FilePath);
            Assert.Equal("javascript", module.Runtime);
            Assert.Equal("loaded; commands 1; limited runtime", module.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryInvoke_ExecutesJavascriptCommandAndSendsRunnerMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-runner-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "hello");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerCommand("hello", {
                description: "Say hello",
                handler: async (args) => {
                  pi.sendMessage(`Hello ${args}`);
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var handled = store.TryInvoke("/hello Ada Lovelace", out var invocation);

            Assert.True(handled);
            Assert.NotNull(invocation);
            Assert.False(invocation.IsError);
            Assert.True(invocation.SendToRunner);
            Assert.Equal("Hello Ada Lovelace", invocation.Message);
            Assert.Equal("javascript", invocation.Command.Runtime);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryInvoke_JavascriptCommandCanReturnStatusText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-status-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "status");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerCommand("status", {
                description: "Return status",
                handler: async (args) => `OK ${args}`
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var handled = store.TryInvoke("/status ready", out var invocation);

            Assert.True(handled);
            Assert.NotNull(invocation);
            Assert.False(invocation.IsError);
            Assert.False(invocation.SendToRunner);
            Assert.Equal("OK ready", invocation.Message);
            Assert.Equal("javascript", invocation.Command.Runtime);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_ReportsJavascriptLoadFailureDiagnostic()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-failure-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "broken");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function() {
              throw new Error("boom");
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var status = store.LoadStatus();

            Assert.Empty(status.Commands);
            var diagnostic = Assert.Single(status.Diagnostics);
            Assert.Equal("error", diagnostic.Severity);
            Assert.Equal(Path.Combine(extensionDirectory, "index.js"), diagnostic.Path);
            Assert.Equal("project", diagnostic.Scope);
            Assert.Contains("failed to load javascript extension:", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("boom", diagnostic.Message, StringComparison.Ordinal);
            var module = Assert.Single(status.Modules);
            Assert.Equal("javascript", module.Runtime);
            Assert.Equal("load failed", module.Status);
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

    private static CodingAgentExtensionCommandStore CreateJavaScriptStore(string directory)
    {
        Assert.True(IsNodeAvailable(), "node is required for javascript extension runtime tests");
        return new CodingAgentExtensionCommandStore(
            cwd: directory,
            userExtensionsDirectory: Path.Combine(directory, "missing-user-extensions"),
            javaScriptRuntime: new CodingAgentJavaScriptExtensionRuntime(directory, nodeExecutable: "node"));
    }

    private static void WriteJavaScriptExtension(string extensionDirectory, string source)
    {
        File.WriteAllText(
            Path.Combine(extensionDirectory, "package.json"),
            """
            {
              "type": "module",
              "pi": {
                "extensions": ["index.js"]
              }
            }
            """);
        File.WriteAllText(Path.Combine(extensionDirectory, "index.js"), source);
    }

    private static bool IsNodeAvailable()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("--version");
        try
        {
            process.Start();
            return process.WaitForExit(2000) && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
