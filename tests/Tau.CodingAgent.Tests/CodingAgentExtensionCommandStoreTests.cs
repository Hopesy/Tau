using System.Diagnostics;
using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
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
                module.Runtime == "typescript" && module.Status == "loaded; commands 0; tools 0; limited runtime");
            Assert.Contains(status.Modules, static module =>
                module.Runtime == "javascript" && module.Status == "loaded; commands 0; tools 0; limited runtime");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_LoadsTypescriptRegisteredCommands()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-ts-load-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "hello-ts");
        Directory.CreateDirectory(extensionDirectory);
        WriteTypeScriptExtension(
            extensionDirectory,
            """
            import { greeting } from "./helper.ts";

            interface CommandMetadata {
              name: string;
            }

            type CommandArgs = string;

            export default function(pi: { registerCommand: Function }) {
              const metadata: CommandMetadata = { name: "hello-ts" };
              pi.registerCommand(metadata.name, {
                description: "Say hello from TS",
                argumentHint: "<name>",
                handler: async (args: CommandArgs, ctx: any) => {
                  ctx.sendMessage(`${greeting} ${args}`);
                }
              });
            }
            """,
            """
            export type Greeting = string;
            export const greeting: Greeting = "Hello";
            """);

        try
        {
            var store = CreateTypeScriptStore(directory);

            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            var command = Assert.Single(status.Commands);
            Assert.Equal("hello-ts", command.Name);
            Assert.Equal("hello-ts", command.InvocationName);
            Assert.Equal("Say hello from TS", command.Description);
            Assert.Equal("<name>", command.ArgumentHint);
            Assert.Equal("project", command.Scope);
            Assert.Equal("typescript", command.Runtime);
            Assert.False(command.SendToRunner);

            var module = Assert.Single(status.Modules);
            Assert.Equal(Path.Combine(extensionDirectory, "index.ts"), module.FilePath);
            Assert.Equal("typescript", module.Runtime);
            Assert.Equal("loaded; commands 1; tools 0; limited runtime", module.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryInvoke_ExecutesTypescriptCommandAndSendsRunnerMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-ts-runner-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "hello-ts");
        Directory.CreateDirectory(extensionDirectory);
        WriteTypeScriptExtension(
            extensionDirectory,
            """
            interface PiApi {
              registerCommand(name: string, options: unknown): void;
              sendMessage(message: string): void;
            }

            export default function(pi: PiApi) {
              pi.registerCommand("hello-ts", {
                description: "Say hello from TS",
                handler: async (args: string) => {
                  pi.sendMessage(`Hello ${args}`);
                }
              });
            }
            """);

        try
        {
            var store = CreateTypeScriptStore(directory);

            var handled = store.TryInvoke("/hello-ts Ada Lovelace", out var invocation);

            Assert.True(handled);
            Assert.NotNull(invocation);
            Assert.False(invocation.IsError);
            Assert.True(invocation.SendToRunner);
            Assert.Equal("Hello Ada Lovelace", invocation.Message);
            Assert.Equal("typescript", invocation.Command.Runtime);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_LoadsTypescriptPackageModuleUnderNodeModules()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-ts-node-modules-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "npm", "node_modules", "hello-ts-package");
        Directory.CreateDirectory(extensionDirectory);
        WriteTypeScriptExtension(
            extensionDirectory,
            """
            export default function(pi: any) {
              pi.registerCommand("hello-package-ts", {
                description: "Say hello from package TS",
                handler: async () => "package ok"
              });
            }
            """);

        try
        {
            Assert.True(IsNodeTypeScriptRuntimeAvailable(), "node with TypeScript stripping hooks is required for typescript extension runtime tests");
            var store = new CodingAgentExtensionCommandStore(
                cwd: directory,
                userExtensionsDirectory: Path.Combine(directory, "missing-user-extensions"),
                explicitPaths: [extensionDirectory],
                includeDefaults: false,
                javaScriptRuntime: new CodingAgentJavaScriptExtensionRuntime(directory, nodeExecutable: "node"));

            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            var command = Assert.Single(status.Commands);
            Assert.Equal("hello-package-ts", command.Name);
            Assert.Equal("typescript", command.Runtime);
            var module = Assert.Single(status.Modules);
            Assert.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", module.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("typescript", module.Runtime);
            Assert.Equal("loaded; commands 1; tools 0; limited runtime", module.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_ReportsTypescriptLoadFailureDiagnostic()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-ts-failure-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "broken-ts");
        Directory.CreateDirectory(extensionDirectory);
        WriteTypeScriptExtension(
            extensionDirectory,
            """
            type Failure = Error;

            export default function(): void {
              const error: Failure = new Error("ts boom");
              throw error;
            }
            """);

        try
        {
            var store = CreateTypeScriptStore(directory);

            var status = store.LoadStatus();

            Assert.Empty(status.Commands);
            var diagnostic = Assert.Single(status.Diagnostics);
            Assert.Equal("error", diagnostic.Severity);
            Assert.Equal(Path.Combine(extensionDirectory, "index.ts"), diagnostic.Path);
            Assert.Equal("project", diagnostic.Scope);
            Assert.Contains("failed to load typescript extension:", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("ts boom", diagnostic.Message, StringComparison.Ordinal);
            var module = Assert.Single(status.Modules);
            Assert.Equal("typescript", module.Runtime);
            Assert.Equal("load failed", module.Status);
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
            Assert.Equal("loaded; commands 1; tools 0; limited runtime", module.Status);
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
    public void LoadStatus_LoadsJavascriptRegisteredTools()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-tool-load-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "hello-tool");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerTool({
                name: "hello_tool",
                label: "Hello Tool",
                description: "Say hello from an extension tool",
                parameters: {
                  type: "object",
                  properties: {
                    name: { type: "string" }
                  },
                  required: ["name"]
                },
                executionMode: "sequential",
                execute: async (toolCallId, params) => {
                  return { content: [{ type: "text", text: `Hello ${params.name} from ${toolCallId}` }] };
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            Assert.Empty(status.Commands);
            var tool = Assert.Single(status.Tools);
            Assert.Equal("hello_tool", tool.Name);
            Assert.Equal("Hello Tool", tool.Label);
            Assert.Equal("Say hello from an extension tool", tool.Description);
            Assert.Equal("project", tool.Scope);
            Assert.Equal("javascript", tool.Runtime);
            Assert.Equal("sequential", tool.ExecutionMode);
            Assert.False(tool.HasPrepareArguments);
            Assert.Equal("object", tool.ParameterSchema.GetProperty("type").GetString());
            Assert.True(tool.ParameterSchema.GetProperty("properties").TryGetProperty("name", out _));
            var module = Assert.Single(status.Modules);
            Assert.Equal("loaded; commands 0; tools 1; limited runtime", module.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadTools_ExecutesJavascriptRegisteredTool()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-tool-run-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "hello-tool");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerTool({
                name: "hello_tool",
                label: "Hello Tool",
                description: "Say hello from an extension tool",
                parameters: {
                  type: "object",
                  properties: {
                    name: { type: "string" }
                  },
                  required: ["name"]
                },
                executionMode: "sequential",
                execute: async (toolCallId, params, signal, onUpdate, ctx) => {
                  return {
                    content: [{ type: "text", text: `Hello ${params.name} from ${toolCallId} in ${ctx.cwd}` }],
                    details: { source: "extension" }
                  };
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);
            var tool = Assert.Single(store.LoadTools());
            using var args = JsonDocument.Parse("""{"name":"Ada"}""");

            var result = await tool.ExecuteAsync("tool-call-1", args.RootElement);

            Assert.False(result.IsError);
            Assert.Equal(ToolExecutionMode.Sequential, tool.ExecutionMode);
            var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
            Assert.Contains("Hello Ada from tool-call-1", text, StringComparison.Ordinal);
            Assert.Contains(directory, text, StringComparison.Ordinal);
            var details = Assert.IsType<JsonElement>(result.Details);
            Assert.Equal("extension", details.GetProperty("source").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadTools_PreparesJavascriptRegisteredToolArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-tool-prepare-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "prepare-tool");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerTool({
                name: "prepare_tool",
                label: "Prepare Tool",
                description: "Prepare raw tool arguments",
                parameters: {
                  type: "object",
                  properties: {
                    name: { type: "string" },
                    prepared: { type: "boolean" }
                  },
                  required: ["name", "prepared"]
                },
                prepareArguments: (args) => ({
                  name: String(args.name ?? "").toUpperCase(),
                  prepared: true
                }),
                execute: async (_toolCallId, params) => `${params.name}:${params.prepared}`
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);
            var definition = Assert.Single(store.LoadToolDefinitions());
            Assert.True(definition.HasPrepareArguments);
            var tool = Assert.Single(store.LoadTools());
            using var rawArgs = JsonDocument.Parse("""{"name":"ada"}""");

            var preparedArgs = await tool.PrepareArgumentsAsync(rawArgs.RootElement);
            var result = await tool.ExecuteAsync("tool-call-1", preparedArgs);

            Assert.Equal("ADA", preparedArgs.GetProperty("name").GetString());
            Assert.True(preparedArgs.GetProperty("prepared").GetBoolean());
            Assert.False(result.IsError);
            Assert.Equal("ADA:true", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_LoadsTypescriptRegisteredTools()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-ts-tool-load-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "ts-tool");
        Directory.CreateDirectory(extensionDirectory);
        WriteTypeScriptExtension(
            extensionDirectory,
            """
            interface ToolParams {
              name: string;
            }

            export default function(pi: any) {
              pi.registerTool({
                name: "ts_tool",
                label: "TS Tool",
                description: "Run a TypeScript tool",
                parameters: {
                  type: "object",
                  properties: {
                    name: { type: "string" }
                  },
                  required: ["name"]
                },
                execute: async (_toolCallId: string, params: ToolParams) => `TS ${params.name}`
              });
            }
            """);

        try
        {
            var store = CreateTypeScriptStore(directory);

            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            var tool = Assert.Single(status.Tools);
            Assert.Equal("ts_tool", tool.Name);
            Assert.Equal("TS Tool", tool.Label);
            Assert.Equal("Run a TypeScript tool", tool.Description);
            Assert.Equal("typescript", tool.Runtime);
            Assert.Equal("object", tool.ParameterSchema.GetProperty("type").GetString());
            var module = Assert.Single(status.Modules);
            Assert.Equal("loaded; commands 0; tools 1; limited runtime", module.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadTools_PreparesTypescriptRegisteredToolArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-ts-tool-prepare-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "ts-tool-prepare");
        Directory.CreateDirectory(extensionDirectory);
        WriteTypeScriptExtension(
            extensionDirectory,
            """
            interface ToolParams {
              name: string;
              prepared: boolean;
            }

            export default function(pi: any) {
              pi.registerTool({
                name: "ts_prepare_tool",
                label: "TS Prepare Tool",
                description: "Prepare raw TypeScript tool arguments",
                parameters: {
                  type: "object",
                  properties: {
                    name: { type: "string" },
                    prepared: { type: "boolean" }
                  },
                  required: ["name", "prepared"]
                },
                prepareArguments: (args: unknown): ToolParams => ({
                  name: String((args as any).name ?? "").toUpperCase(),
                  prepared: true
                }),
                execute: async (_toolCallId: string, params: ToolParams) => `TS ${params.name}:${params.prepared}`
              });
            }
            """);

        try
        {
            var store = CreateTypeScriptStore(directory);
            var definition = Assert.Single(store.LoadToolDefinitions());
            Assert.True(definition.HasPrepareArguments);
            var tool = Assert.Single(store.LoadTools());
            using var rawArgs = JsonDocument.Parse("""{"name":"ada"}""");

            var preparedArgs = await tool.PrepareArgumentsAsync(rawArgs.RootElement);
            var result = await tool.ExecuteAsync("tool-call-1", preparedArgs);

            Assert.Equal("ADA", preparedArgs.GetProperty("name").GetString());
            Assert.True(preparedArgs.GetProperty("prepared").GetBoolean());
            Assert.False(result.IsError);
            Assert.Equal("TS ADA:true", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadTools_UsesFirstRegisteredExtensionToolName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-tool-duplicates-" + Guid.NewGuid().ToString("N"));
        var firstExtension = Path.Combine(directory, ".tau", "extensions", "a-first");
        var secondExtension = Path.Combine(directory, ".tau", "extensions", "b-second");
        Directory.CreateDirectory(firstExtension);
        Directory.CreateDirectory(secondExtension);
        WriteJavaScriptExtension(
            firstExtension,
            """
            export default function(pi) {
              pi.registerTool({
                name: "duplicate_tool",
                label: "First",
                description: "First tool",
                parameters: { type: "object" },
                execute: async () => "first"
              });
            }
            """);
        WriteJavaScriptExtension(
            secondExtension,
            """
            export default function(pi) {
              pi.registerTool({
                name: "duplicate_tool",
                label: "Second",
                description: "Second tool",
                parameters: { type: "object" },
                execute: async () => "second"
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var status = store.LoadStatus();

            var definition = Assert.Single(status.Tools);
            Assert.Equal("First", definition.Label);
            Assert.Equal(2, status.Modules.Count);

            var tool = Assert.Single(store.LoadTools());
            using var args = JsonDocument.Parse("{}");
            var result = await tool.ExecuteAsync("tool-call-1", args.RootElement);

            Assert.Equal("first", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
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

    private static CodingAgentExtensionCommandStore CreateTypeScriptStore(string directory)
    {
        Assert.True(IsNodeTypeScriptRuntimeAvailable(), "node with TypeScript stripping hooks is required for typescript extension runtime tests");
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

    private static void WriteTypeScriptExtension(string extensionDirectory, string source, string? helperSource = null)
    {
        File.WriteAllText(
            Path.Combine(extensionDirectory, "package.json"),
            """
            {
              "type": "module",
              "pi": {
                "extensions": ["index.ts"]
              }
            }
            """);
        File.WriteAllText(Path.Combine(extensionDirectory, "index.ts"), source);
        if (helperSource is not null)
        {
            File.WriteAllText(Path.Combine(extensionDirectory, "helper.ts"), helperSource);
        }
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

    private static bool IsNodeTypeScriptRuntimeAvailable()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add("const m = require('node:module'); process.exit(typeof m.registerHooks === 'function' && typeof m.stripTypeScriptTypes === 'function' ? 0 : 1);");
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
