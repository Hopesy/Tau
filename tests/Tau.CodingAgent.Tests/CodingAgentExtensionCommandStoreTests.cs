using System.Diagnostics;
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

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
    public async Task LoadStatus_LoadsJavascriptExtensionWithVirtualPackageImports()
    {
        Assert.True(IsNodeTypeScriptRuntimeAvailable(), "node module hooks are required for virtual extension package imports");
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-virtual-imports-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "virtual-imports");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            import { Type, calculateCost, getModels, registerModel } from "@mariozechner/pi-ai";
            import { getOAuthProviders, registerOAuthProvider } from "@mariozechner/pi-ai/oauth";
            import { ToolExecutionMode } from "@mariozechner/pi-agent-core";
            import { defineTool } from "@mariozechner/pi-coding-agent";
            import { Text } from "@mariozechner/pi-tui";

            export default function(pi) {
              registerModel("tau", { id: "tau-model", cost: { input: 1000000, output: 0 } });
              registerOAuthProvider({ id: "tau-oauth", name: "Tau OAuth" });
              const text = new Text("Tau").render().join("");
              const parameters = Type.Object({
                name: Type.String(),
                optional: Type.Optional(Type.Boolean())
              });

              pi.registerCommand("virtual-info", {
                description: "Read virtual extension package state",
                handler: async () => {
                  const cost = calculateCost({ cost: { input: 1000000, output: 0 } }, { input: 2, output: 0 });
                  return [
                    parameters.required.join(","),
                    getModels("tau").map(model => model.id).join(","),
                    getOAuthProviders().map(provider => provider.id).join(","),
                    String(cost.total),
                    text
                  ].join(":");
                }
              });

              pi.registerTool(defineTool({
                name: "virtual_tool",
                label: "Virtual Tool",
                description: "Exercise virtual package imports",
                parameters,
                executionMode: ToolExecutionMode.Sequential,
                execute: async (_toolCallId, params) => `virtual ${params.name}`
              }));
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            var command = Assert.Single(status.Commands);
            Assert.Equal("virtual-info", command.Name);
            var toolDefinition = Assert.Single(status.Tools);
            Assert.Equal("virtual_tool", toolDefinition.Name);
            Assert.Equal("object", toolDefinition.ParameterSchema.GetProperty("type").GetString());
            Assert.Equal(
                ["name"],
                toolDefinition.ParameterSchema.GetProperty("required").EnumerateArray().Select(static item => item.GetString()!).ToArray());
            var module = Assert.Single(status.Modules);
            Assert.Equal("loaded; commands 1; tools 1; limited runtime", module.Status);

            var handled = store.TryInvoke("/virtual-info", out var invocation);

            Assert.True(handled);
            Assert.NotNull(invocation);
            Assert.False(invocation.IsError);
            Assert.Equal("name:tau-model:tau-oauth:2:Tau", invocation.Message);

            var tool = Assert.Single(store.LoadTools());
            using var args = JsonDocument.Parse("""{"name":"Ada"}""");
            var result = await tool.ExecuteAsync("tool-call-1", args.RootElement);

            Assert.False(result.IsError);
            Assert.Equal("virtual Ada", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
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
    public void LoadStatus_LoadsJavascriptRegisteredFlagsAndGetFlagDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-flags-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "flags");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerFlag("enabled", {
                description: "Enable feature",
                type: "boolean",
                default: true
              });
              pi.registerFlag("mode", {
                description: "Execution mode",
                type: "string",
                default: "slow"
              });
              pi.registerCommand("flag-check", {
                description: "Read flags",
                handler: async () => {
                  return `enabled=${pi.getFlag("enabled")}; mode=${pi.getFlag("mode")}; missing=${pi.getFlag("missing") === undefined}`;
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            Assert.Equal(2, status.Flags.Count);
            var enabled = Assert.Single(status.Flags, static flag => flag.Name == "enabled");
            Assert.Equal("Enable feature", enabled.Description);
            Assert.Equal("boolean", enabled.Type);
            Assert.NotNull(enabled.DefaultValue);
            Assert.True(enabled.DefaultValue.Value.GetBoolean());
            Assert.Equal("project", enabled.Scope);
            Assert.Equal("javascript", enabled.Runtime);

            var mode = Assert.Single(status.Flags, static flag => flag.Name == "mode");
            Assert.Equal("Execution mode", mode.Description);
            Assert.Equal("string", mode.Type);
            Assert.NotNull(mode.DefaultValue);
            Assert.Equal("slow", mode.DefaultValue.Value.GetString());

            var module = Assert.Single(status.Modules);
            Assert.Equal("loaded; commands 1; tools 0; flags 2; limited runtime", module.Status);

            var handled = store.TryInvoke("/flag-check", out var invocation);
            Assert.True(handled);
            Assert.NotNull(invocation);
            Assert.False(invocation.IsError);
            Assert.False(invocation.SendToRunner);
            Assert.Equal("enabled=true; mode=slow; missing=true", invocation.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ApplyExtensionFlagValues_InjectsCliFlagValuesIntoGetFlag()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-cli-flags-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "flags");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerFlag("plan", {
                description: "Enable plan mode",
                type: "boolean",
                default: false
              });
              pi.registerFlag("mode", {
                description: "Execution mode",
                type: "string",
                default: "slow"
              });
              pi.registerCommand("flag-check", {
                description: "Read flags",
                handler: async () => {
                  return `plan=${pi.getFlag("plan")}; mode=${pi.getFlag("mode")}`;
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var diagnostics = store.ApplyExtensionFlagValues(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["plan"] = null,
                ["mode"] = "fast"
            });

            Assert.Empty(diagnostics);

            var handled = store.TryInvoke("/flag-check", out var invocation);
            Assert.True(handled);
            Assert.NotNull(invocation);
            Assert.False(invocation.IsError);
            Assert.Equal("plan=true; mode=fast", invocation.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ApplyExtensionFlagValues_ReportsUnknownAndValuelessStringFlags()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-cli-flag-errors-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "flags");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerFlag("mode", {
                description: "Execution mode",
                type: "string"
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var diagnostics = store.ApplyExtensionFlagValues(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["mode"] = null,
                ["unknown"] = null
            });

            Assert.Equal(2, diagnostics.Count);
            Assert.Contains(diagnostics, static d =>
                d.Severity == "error" && d.Message == "Extension flag \"--mode\" requires a value");
            Assert.Contains(diagnostics, static d =>
                d.Severity == "error" && d.Message == "Unknown option: --unknown");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_LoadsJavascriptRegisteredShortcuts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-shortcuts-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "shortcuts");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerShortcut("ctrl+x", {
                description: "Run extension shortcut",
                handler: async (ctx) => {
                  ctx.sendMessage("shortcut handled");
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            var shortcut = Assert.Single(status.Shortcuts);
            Assert.Equal("ctrl+x", shortcut.Shortcut);
            Assert.Equal("Run extension shortcut", shortcut.Description);
            Assert.True(shortcut.HasHandler);
            Assert.Equal("project", shortcut.Scope);
            Assert.Equal("javascript", shortcut.Runtime);
            var module = Assert.Single(status.Modules);
            Assert.Equal("loaded; commands 0; tools 0; shortcuts 1; limited runtime", module.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryInvokeShortcut_JavascriptHandlerCanSendRunnerMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-shortcut-invoke-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "shortcuts");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerShortcut("ctrl+x", {
                description: "Run extension shortcut",
                handler: async (ctx) => {
                  ctx.sendMessage("shortcut handled");
                  return "shortcut status";
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);
            var shortcut = Assert.Single(store.LoadStatus().Shortcuts);

            Assert.True(store.TryInvokeShortcut(shortcut, out var invocation));

            Assert.NotNull(invocation);
            Assert.False(invocation.IsError);
            Assert.True(invocation.SendToRunner);
            Assert.Equal("shortcut handled", invocation.Message);
            Assert.Equal("ctrl+x", invocation.Shortcut.Shortcut);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_WithKeyBindingsSkipsBuiltInShortcutConflict()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-shortcut-conflict-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "shortcuts");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerShortcut("ctrl+p", {
                description: "Conflicts with built-in model cycle",
                handler: async () => {}
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var status = store.LoadStatus(KeyBindingMap.Default);

            Assert.Empty(status.Shortcuts);
            var diagnostic = Assert.Single(status.Diagnostics);
            Assert.Equal("warning", diagnostic.Severity);
            Assert.Contains("conflicts with built-in shortcut", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadStatus_WithKeyBindingsAllowsNonReservedBuiltInShortcutOverride()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-shortcut-nonreserved-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "shortcuts");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerShortcut("ctrl+u", {
                description: "Override non-reserved edit binding",
                handler: async () => {}
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);

            var status = store.LoadStatus(KeyBindingMap.Default);

            var shortcut = Assert.Single(status.Shortcuts);
            Assert.Equal("ctrl+u", shortcut.Shortcut);
            var diagnostic = Assert.Single(status.Diagnostics);
            Assert.Equal("warning", diagnostic.Severity);
            Assert.Contains("Using", diagnostic.Message, StringComparison.Ordinal);
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
    public async Task LoadToolInterceptors_BlocksJavascriptToolCallHandler()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-tool-call-hook-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "tool-call-hook");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.on("tool_call", (event) => {
                if (event.toolName === "hello_tool") {
                  return { block: true, reason: `blocked ${event.input.name}` };
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);
            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            Assert.Equal("tool_call", Assert.Single(status.EventHandlers).EventType);
            Assert.Equal("loaded; commands 0; tools 0; events 1; limited runtime", Assert.Single(status.Modules).Status);

            var interceptor = Assert.Single(store.LoadToolInterceptors());
            using var args = JsonDocument.Parse("""{"name":"Ada"}""");

            var decision = await interceptor.BeforeToolCallAsync(
                new ToolCallContext("tool-call-1", "hello_tool", args.RootElement, []));

            Assert.True(decision.Blocked);
            Assert.Equal("blocked Ada", decision.Reason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadToolInterceptors_MutatesJavascriptToolCallInputAcrossModules()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-tool-call-mutation-" + Guid.NewGuid().ToString("N"));
        var firstExtension = Path.Combine(directory, ".tau", "extensions", "a-first-hook");
        var secondExtension = Path.Combine(directory, ".tau", "extensions", "b-second-hook");
        Directory.CreateDirectory(firstExtension);
        Directory.CreateDirectory(secondExtension);
        WriteJavaScriptExtension(
            firstExtension,
            """
            export default function(pi) {
              pi.on("tool_call", (event) => {
                if (event.toolName === "hello_tool") {
                  event.input.name = String(event.input.name ?? "").toUpperCase();
                }
              });
            }
            """);
        WriteJavaScriptExtension(
            secondExtension,
            """
            export default function(pi) {
              pi.on("tool_call", (event) => {
                if (event.toolName === "hello_tool") {
                  event.input.seenBySecondHook = event.input.name;
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);
            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            Assert.Equal(2, status.EventHandlers.Count);

            var interceptor = Assert.Single(store.LoadToolInterceptors());
            using var args = JsonDocument.Parse("""{"name":"ada"}""");

            var decision = await interceptor.BeforeToolCallAsync(
                new ToolCallContext("tool-call-1", "hello_tool", args.RootElement, []));

            Assert.False(decision.Blocked);
            Assert.True(decision.Arguments.HasValue);
            Assert.Equal("ADA", decision.Arguments.Value.GetProperty("name").GetString());
            Assert.Equal("ADA", decision.Arguments.Value.GetProperty("seenBySecondHook").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadToolInterceptors_RewritesJavascriptToolResultHandler()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-tool-result-hook-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "tool-result-hook");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.on("tool_result", (event) => {
                if (event.toolName === "hello_tool") {
                  return {
                    content: [{ type: "text", text: `${event.content[0].text} patched for ${event.input.name}` }],
                    details: { patched: true, original: event.details.source },
                    isError: true
                  };
                }
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);
            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            Assert.Equal("tool_result", Assert.Single(status.EventHandlers).EventType);

            var interceptor = Assert.Single(store.LoadToolInterceptors());
            using var args = JsonDocument.Parse("""{"name":"Ada"}""");
            using var details = JsonDocument.Parse("""{"source":"extension"}""");
            var original = new ToolResult(
                [new TextContent("original")],
                IsError: false,
                Details: details.RootElement.Clone());

            var result = await interceptor.AfterToolCallAsync(
                new ToolCallContext("tool-call-1", "hello_tool", args.RootElement, []),
                original);

            Assert.True(result.IsError);
            Assert.Equal("original patched for Ada", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
            var resultDetails = Assert.IsType<JsonElement>(result.Details);
            Assert.True(resultDetails.GetProperty("patched").GetBoolean());
            Assert.Equal("extension", resultDetails.GetProperty("original").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadLifecycleEventSink_EmitsJavascriptMessageLifecycleHandler()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-js-lifecycle-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "lifecycle");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            import fs from "node:fs";

            export default function(pi) {
              pi.on("message_start", (event) => {
                const first = event.message?.content?.[0]?.text ?? "";
                fs.appendFileSync("events.log", `${event.type}:${event.message.role}:${first}\n`);
              });
            }
            """);

        try
        {
            var store = CreateJavaScriptStore(directory);
            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            Assert.Equal("message_start", Assert.Single(status.EventHandlers).EventType);
            Assert.Equal("loaded; commands 0; tools 0; events 1; limited runtime", Assert.Single(status.Modules).Status);

            var sink = store.LoadLifecycleEventSink();
            Assert.NotNull(sink);
            await sink!.PublishAsync(new MessageStartEvent(new UserMessage("hello lifecycle")));

            var logPath = Path.Combine(directory, "events.log");
            Assert.Equal("message_start:user:hello lifecycle\n", File.ReadAllText(logPath).ReplaceLineEndings("\n"));
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
    public void LoadStatus_LoadsTypescriptRegisteredFlagsAndShortcuts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tau-extensions-ts-flags-shortcuts-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(directory, ".tau", "extensions", "ts-flags-shortcuts");
        Directory.CreateDirectory(extensionDirectory);
        WriteTypeScriptExtension(
            extensionDirectory,
            """
            export default function(pi: any) {
              pi.registerFlag("dry-run", {
                description: "Preview only",
                type: "boolean",
                default: false
              });
              pi.registerShortcut("ctrl+shift+d", {
                description: "Toggle dry run",
                handler: async () => {}
              });
            }
            """);

        try
        {
            var store = CreateTypeScriptStore(directory);

            var status = store.LoadStatus();

            Assert.Empty(status.Diagnostics);
            var flag = Assert.Single(status.Flags);
            Assert.Equal("dry-run", flag.Name);
            Assert.Equal("Preview only", flag.Description);
            Assert.Equal("boolean", flag.Type);
            Assert.NotNull(flag.DefaultValue);
            Assert.False(flag.DefaultValue.Value.GetBoolean());
            Assert.Equal("typescript", flag.Runtime);

            var shortcut = Assert.Single(status.Shortcuts);
            Assert.Equal("ctrl+shift+d", shortcut.Shortcut);
            Assert.Equal("Toggle dry run", shortcut.Description);
            Assert.True(shortcut.HasHandler);
            Assert.Equal("typescript", shortcut.Runtime);

            var module = Assert.Single(status.Modules);
            Assert.Equal("loaded; commands 0; tools 0; flags 1; shortcuts 1; limited runtime", module.Status);
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
