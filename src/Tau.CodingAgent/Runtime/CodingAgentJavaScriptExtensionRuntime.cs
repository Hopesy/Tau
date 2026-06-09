using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentJavaScriptExtensionCommand(
    string Name,
    string Description,
    string? ArgumentHint,
    bool HasHandler);

public sealed record CodingAgentJavaScriptExtensionTool(
    string Name,
    string Label,
    string Description,
    JsonElement ParameterSchema,
    bool HasHandler,
    string? ExecutionMode);

public sealed record CodingAgentJavaScriptExtensionUnsupportedRegistrations(
    int Tools,
    int Flags,
    int Shortcuts,
    int Handlers,
    int MessageRenderers,
    int Providers);

public sealed record CodingAgentJavaScriptExtensionLoadResult(
    bool Success,
    IReadOnlyList<CodingAgentJavaScriptExtensionCommand> Commands,
    IReadOnlyList<CodingAgentJavaScriptExtensionTool> Tools,
    CodingAgentJavaScriptExtensionUnsupportedRegistrations Unsupported,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionInvokeResult(
    bool Success,
    IReadOnlyList<string> RunnerMessages,
    string? StatusMessage,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionToolInvokeResult(
    bool Success,
    IReadOnlyList<string> Content,
    bool IsError,
    JsonElement? Details,
    string? Error);

public sealed class CodingAgentJavaScriptExtensionRuntime
{
    public const string NodeExecutableEnvironmentVariable = "TAU_CODING_AGENT_NODE";

    private const string ResultPrefix = "__TAU_EXTENSION_RESULT__";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly string _cwd;
    private readonly string? _nodeExecutable;
    private readonly TimeSpan _timeout;

    public CodingAgentJavaScriptExtensionRuntime(
        string? cwd = null,
        string? nodeExecutable = null,
        TimeSpan? timeout = null)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        _nodeExecutable = string.IsNullOrWhiteSpace(nodeExecutable)
            ? Environment.GetEnvironmentVariable(NodeExecutableEnvironmentVariable)
            : nodeExecutable;
        _timeout = timeout ?? DefaultTimeout;
    }

    public CodingAgentJavaScriptExtensionLoadResult Load(string filePath)
    {
        var execution = Execute(BuildPayload("load", filePath, _cwd));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionLoadResult(false, [], [], EmptyUnsupported, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionLoadResult(
                    false,
                    [],
                    [],
                    ReadUnsupported(root),
                    ReadString(root, "error") ?? "javascript extension load failed");
            }

            return new CodingAgentJavaScriptExtensionLoadResult(
                true,
                ReadCommands(root),
                ReadTools(root),
                ReadUnsupported(root),
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionLoadResult(
                false,
                [],
                [],
                EmptyUnsupported,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    public CodingAgentJavaScriptExtensionInvokeResult Invoke(
        string filePath,
        string commandName,
        string args)
    {
        var execution = Execute(BuildPayload("invoke", filePath, _cwd, commandName, args));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionInvokeResult(false, [], null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionInvokeResult(
                    false,
                    [],
                    null,
                    ReadString(root, "error") ?? "javascript extension command failed");
            }

            return new CodingAgentJavaScriptExtensionInvokeResult(
                true,
                ReadRunnerMessages(root),
                ReadString(root, "returnText"),
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionInvokeResult(
                false,
                [],
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    public CodingAgentJavaScriptExtensionToolInvokeResult ExecuteTool(
        string filePath,
        string toolName,
        string toolCallId,
        JsonElement args)
    {
        var execution = Execute(BuildPayload(
            "executeTool",
            filePath,
            _cwd,
            toolName: toolName,
            toolCallId: toolCallId,
            toolArgs: args));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionToolInvokeResult(false, [], true, null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionToolInvokeResult(
                    false,
                    [],
                    true,
                    null,
                    ReadString(root, "error") ?? "javascript extension tool failed");
            }

            return new CodingAgentJavaScriptExtensionToolInvokeResult(
                true,
                ReadStringArray(root, "content"),
                ReadBool(root, "isError"),
                root.TryGetProperty("details", out var details) ? details.Clone() : null,
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionToolInvokeResult(
                false,
                [],
                true,
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    private ProcessExecutionResult Execute(string payloadJson)
    {
        var executable = string.IsNullOrWhiteSpace(_nodeExecutable) ? "node" : _nodeExecutable!;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(NodeScript);
        if (Directory.Exists(_cwd))
        {
            process.StartInfo.WorkingDirectory = _cwd;
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return ProcessExecutionResult.Failed($"node extension runtime unavailable: {ex.Message}");
        }

        process.StandardInput.Write(payloadJson);
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
        {
            TryKill(process);
            Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(1));
            return ProcessExecutionResult.Failed("node extension runtime timed out");
        }

        Task.WaitAll(stdoutTask, stderrTask);
        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        var resultJson = ExtractResultJson(stdout);
        if (resultJson is null)
        {
            var suffix = process.ExitCode == 0
                ? "runtime did not return a result"
                : $"runtime exited with code {process.ExitCode}";
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                suffix = $"{suffix}: {TrimForDiagnostic(stderr)}";
            }

            return ProcessExecutionResult.Failed($"invalid node extension runtime output: {suffix}");
        }

        return ProcessExecutionResult.Succeeded(resultJson);
    }

    private static string BuildPayload(
        string mode,
        string filePath,
        string cwd,
        string? commandName = null,
        string? args = null,
        string? toolName = null,
        string? toolCallId = null,
        JsonElement? toolArgs = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("mode", mode);
            writer.WriteString("filePath", Path.GetFullPath(filePath));
            writer.WriteString("cwd", cwd);
            if (commandName is not null)
            {
                writer.WriteString("commandName", commandName);
            }

            if (args is not null)
            {
                writer.WriteString("args", args);
            }

            if (toolName is not null)
            {
                writer.WriteString("toolName", toolName);
            }

            if (toolCallId is not null)
            {
                writer.WriteString("toolCallId", toolCallId);
            }

            if (toolArgs.HasValue)
            {
                writer.WritePropertyName("toolArgs");
                toolArgs.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyList<CodingAgentJavaScriptExtensionCommand> ReadCommands(JsonElement root)
    {
        if (!root.TryGetProperty("commands", out var commandsElement) ||
            commandsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var commands = new List<CodingAgentJavaScriptExtensionCommand>();
        foreach (var command in commandsElement.EnumerateArray())
        {
            if (command.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(command, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            commands.Add(new CodingAgentJavaScriptExtensionCommand(
                name,
                ReadString(command, "description") ?? string.Empty,
                ReadString(command, "argumentHint"),
                ReadBool(command, "hasHandler")));
        }

        return commands.ToArray();
    }

    private static IReadOnlyList<CodingAgentJavaScriptExtensionTool> ReadTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var toolsElement) ||
            toolsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tools = new List<CodingAgentJavaScriptExtensionTool>();
        foreach (var tool in toolsElement.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(tool, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            tools.Add(new CodingAgentJavaScriptExtensionTool(
                name,
                ReadString(tool, "label") ?? name,
                ReadString(tool, "description") ?? string.Empty,
                ReadParameterSchema(tool),
                ReadBool(tool, "hasHandler"),
                ReadString(tool, "executionMode")));
        }

        return tools.ToArray();
    }

    private static JsonElement ReadParameterSchema(JsonElement tool)
    {
        if (tool.TryGetProperty("parameters", out var parameters) &&
            parameters.ValueKind == JsonValueKind.Object)
        {
            return parameters.Clone();
        }

        using var document = JsonDocument.Parse("""{"type":"object"}""");
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<string> ReadRunnerMessages(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actionsElement) ||
            actionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var messages = new List<string>();
        foreach (var action in actionsElement.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(action, "type"), "sendMessage", StringComparison.Ordinal))
            {
                continue;
            }

            var message = ReadString(action, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                messages.Add(message);
            }
        }

        return messages.ToArray();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                values.Add(item.GetString() ?? string.Empty);
            }
        }

        return values.ToArray();
    }

    private static CodingAgentJavaScriptExtensionUnsupportedRegistrations ReadUnsupported(JsonElement root)
    {
        if (!root.TryGetProperty("unsupported", out var unsupported) ||
            unsupported.ValueKind != JsonValueKind.Object)
        {
            return EmptyUnsupported;
        }

        return new CodingAgentJavaScriptExtensionUnsupportedRegistrations(
            ReadInt(unsupported, "tools"),
            ReadInt(unsupported, "flags"),
            ReadInt(unsupported, "shortcuts"),
            ReadInt(unsupported, "handlers"),
            ReadInt(unsupported, "messageRenderers"),
            ReadInt(unsupported, "providers"));
    }

    private static string? ExtractResultJson(string stdout)
    {
        string? result = null;
        using var reader = new StringReader(stdout);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(ResultPrefix, StringComparison.Ordinal))
            {
                result = line[ResultPrefix.Length..];
            }
        }

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false
        };
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static string TrimForDiagnostic(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300];
    }

    private static CodingAgentJavaScriptExtensionUnsupportedRegistrations EmptyUnsupported { get; } = new(0, 0, 0, 0, 0, 0);

    private sealed record ProcessExecutionResult(
        bool Success,
        string ResultJson,
        string? Error)
    {
        public static ProcessExecutionResult Succeeded(string resultJson) => new(true, resultJson, null);

        public static ProcessExecutionResult Failed(string error) => new(false, string.Empty, error);
    }

    private const string NodeScript = """
        const fs = require("node:fs");
        const path = require("node:path");
        const moduleApi = require("node:module");
        const { fileURLToPath, pathToFileURL } = require("node:url");
        const resultPrefix = "__TAU_EXTENSION_RESULT__";
        let typeScriptHookInstalled = false;

        let input = "";
        process.stdin.setEncoding("utf8");
        process.stdin.on("data", chunk => { input += chunk; });
        process.stdin.on("end", () => {
          main().catch(error => write({ ok: false, error: formatError(error) }));
        });

        function write(result) {
          process.stdout.write(resultPrefix + JSON.stringify(result) + "\n");
        }

        function formatError(error) {
          return error && error.message ? String(error.message) : String(error);
        }

        function isTypeScriptFile(filePath) {
          return typeof filePath === "string" && filePath.toLowerCase().endsWith(".ts");
        }

        function hasModuleSyntax(source) {
          return /(^|\s)(import|export)\s/.test(source);
        }

        function readNearestPackageType(directory) {
          let current = directory;
          while (current && current !== path.dirname(current)) {
            const packageJsonPath = path.join(current, "package.json");
            try {
              const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8"));
              if (packageJson && packageJson.type === "module") return "module";
              if (packageJson && packageJson.type === "commonjs") return "commonjs";
            } catch {
            }
            current = path.dirname(current);
          }
          return undefined;
        }

        function inferTypeScriptFormat(url, source) {
          const packageType = readNearestPackageType(path.dirname(fileURLToPath(url)));
          if (packageType === "module" || packageType === "commonjs") return packageType;
          return hasModuleSyntax(source) ? "module" : "commonjs";
        }

        function installTypeScriptHook() {
          if (typeScriptHookInstalled) return;
          if (typeof moduleApi.registerHooks !== "function" || typeof moduleApi.stripTypeScriptTypes !== "function") {
            throw new Error("typescript extension runtime unavailable: Node.js type stripping hooks are not available");
          }

          moduleApi.registerHooks({
            load(url, context, nextLoad) {
              const parsed = new URL(url);
              if (parsed.protocol === "file:" && parsed.pathname.toLowerCase().endsWith(".ts")) {
                const source = fs.readFileSync(parsed, "utf8");
                const stripped = moduleApi.stripTypeScriptTypes(source, { mode: "strip", sourceUrl: url });
                return {
                  format: inferTypeScriptFormat(url, source),
                  shortCircuit: true,
                  source: stripped
                };
              }

              return nextLoad(url, context);
            }
          });
          typeScriptHookInstalled = true;
        }

        function toText(value) {
          if (value === undefined || value === null) return "";
          if (typeof value === "string") return value;
          if (Array.isArray(value)) {
            return value.map(item => toText(item)).filter(Boolean).join("\n");
          }
          if (typeof value === "object") {
            if (typeof value.text === "string") return value.text;
            if (typeof value.content === "string") return value.content;
            if (typeof value.message === "string") return value.message;
            return JSON.stringify(value);
          }
          return String(value);
        }

        function normalizeToolContent(value) {
          const text = toText(value);
          return text.length === 0 ? [] : [text];
        }

        function normalizeToolResult(value) {
          if (value && typeof value === "object") {
            const content = Object.prototype.hasOwnProperty.call(value, "content")
              ? normalizeToolContent(value.content)
              : normalizeToolContent(value);
            return {
              content,
              isError: value.isError === true,
              details: Object.prototype.hasOwnProperty.call(value, "details") ? value.details : undefined
            };
          }

          return {
            content: normalizeToolContent(value),
            isError: false,
            details: undefined
          };
        }

        function createApi(commandMap, toolMap, unsupported, actions, payload) {
          const recordMessage = value => {
            const message = toText(value);
            if (message.trim().length > 0) actions.push({ type: "sendMessage", message });
          };
          return {
            registerCommand(name, options = {}) {
              const key = String(name ?? "");
              commandMap.set(key, { name: key, options: options || {} });
            },
            registerTool(tool = {}) {
              const key = String(tool && tool.name !== undefined ? tool.name : "");
              toolMap.set(key, tool || {});
            },
            registerFlag() { unsupported.flags++; },
            registerShortcut() { unsupported.shortcuts++; },
            registerMessageRenderer() { unsupported.messageRenderers++; },
            registerProvider() { unsupported.providers++; },
            unregisterProvider() { unsupported.providers++; },
            on() { unsupported.handlers++; },
            getFlag() { return undefined; },
            sendMessage: recordMessage,
            sendUserMessage: recordMessage,
            appendEntry() {},
            setSessionName() {},
            getSessionName() { return undefined; },
            setLabel() {},
            getActiveTools() { return []; },
            getAllTools() {
              return Array.from(toolMap.values()).map(tool => ({
                name: String(tool && tool.name !== undefined ? tool.name : ""),
                label: typeof tool?.label === "string" ? tool.label : String(tool && tool.name !== undefined ? tool.name : ""),
                description: typeof tool?.description === "string" ? tool.description : "",
                parameters: tool?.parameters && typeof tool.parameters === "object" ? tool.parameters : { type: "object" }
              }));
            },
            setActiveTools() {},
            getCommands() {
              return Array.from(commandMap.values()).map(command => ({
                name: command.name,
                description: typeof command.options.description === "string" ? command.options.description : ""
              }));
            },
            setModel() { return Promise.resolve(false); },
            getThinkingLevel() { return undefined; },
            setThinkingLevel() {},
            exec() { return Promise.reject(new Error("extension exec is not supported by Tau javascript runtime baseline")); },
            events: {
              on() { unsupported.handlers++; return () => {}; },
              emit() {}
            },
            cwd: payload.cwd
          };
        }

        function createCommandContext(api, payload) {
          return {
            ui: {
              select: async () => undefined,
              confirm: async () => false,
              input: async () => undefined,
              notify: () => {},
              onTerminalInput: () => () => {},
              setStatus: () => {},
              setWorkingMessage: () => {},
              setHiddenThinkingLabel: () => {},
              setWidget: () => {},
              setFooter: () => {},
              setHeader: () => {},
              setTitle: () => {},
              custom: async () => undefined,
              pasteToEditor: () => {},
              setEditorText: () => {},
              getEditorText: () => "",
              editor: async () => undefined
            },
            hasUI: false,
            cwd: payload.cwd,
            sessionManager: {},
            modelRegistry: {},
            model: undefined,
            isIdle: () => true,
            signal: undefined,
            abort: () => {},
            hasPendingMessages: () => false,
            shutdown: () => {},
            getContextUsage: () => undefined,
            compact: () => {},
            getSystemPrompt: () => "",
            waitForIdle: async () => {},
            newSession: async () => ({ cancelled: false }),
            fork: async () => ({ cancelled: false }),
            navigateTree: async () => ({ cancelled: false }),
            switchSession: async () => ({ cancelled: false }),
            reload: async () => {},
            sendMessage: api.sendMessage,
            sendUserMessage: api.sendUserMessage
          };
        }

        async function loadFactory(filePath) {
          if (isTypeScriptFile(filePath)) {
            installTypeScriptHook();
          }
          const url = pathToFileURL(filePath).href + "?tauCacheBust=" + Date.now() + "-" + Math.random();
          const module = await import(url);
          let factory = module.default;
          if (factory && typeof factory !== "function" && typeof factory.default === "function") {
            factory = factory.default;
          }
          if (typeof factory !== "function") {
            throw new Error("Extension does not export a valid factory function");
          }
          return factory;
        }

        async function main() {
          const payload = JSON.parse(input || "{}");
          const commandMap = new Map();
          const toolMap = new Map();
          const unsupported = { tools: 0, flags: 0, shortcuts: 0, handlers: 0, messageRenderers: 0, providers: 0 };
          const actions = [];
          const api = createApi(commandMap, toolMap, unsupported, actions, payload);
          const factory = await loadFactory(payload.filePath);
          await factory(api);

          const commands = Array.from(commandMap.values()).map(command => ({
            name: command.name,
            description: typeof command.options.description === "string" ? command.options.description : "",
            argumentHint: typeof command.options.argumentHint === "string" ? command.options.argumentHint : undefined,
            hasHandler: typeof command.options.handler === "function"
          }));

          const tools = Array.from(toolMap.values()).map(tool => ({
            name: String(tool && tool.name !== undefined ? tool.name : ""),
            label: typeof tool?.label === "string" ? tool.label : String(tool && tool.name !== undefined ? tool.name : ""),
            description: typeof tool?.description === "string" ? tool.description : "",
            parameters: tool?.parameters && typeof tool.parameters === "object" ? tool.parameters : { type: "object" },
            hasHandler: typeof tool?.execute === "function",
            executionMode: typeof tool?.executionMode === "string" ? tool.executionMode : undefined
          }));

          if (payload.mode === "load") {
            write({ ok: true, commands, tools, unsupported });
            return;
          }

          if (payload.mode === "executeTool") {
            const tool = toolMap.get(String(payload.toolName ?? ""));
            if (!tool) {
              write({ ok: false, error: "Extension tool was not registered: " + String(payload.toolName ?? "") });
              return;
            }
            if (typeof tool.execute !== "function") {
              write({ ok: false, error: "Extension tool has no execute handler: " + String(tool.name ?? payload.toolName ?? "") });
              return;
            }

            const onUpdate = update => {
              const message = toText(update);
              if (message.trim().length > 0) actions.push({ type: "toolUpdate", message });
            };
            const returnValue = await tool.execute(
              String(payload.toolCallId ?? ""),
              payload.toolArgs && typeof payload.toolArgs === "object" ? payload.toolArgs : {},
              undefined,
              onUpdate,
              createCommandContext(api, payload));
            const result = normalizeToolResult(returnValue);
            write({ ok: true, content: result.content, isError: result.isError, details: result.details, actions, unsupported });
            return;
          }

          const command = commandMap.get(String(payload.commandName ?? ""));
          if (!command) {
            write({ ok: false, error: "Extension command was not registered: " + String(payload.commandName ?? "") });
            return;
          }
          if (typeof command.options.handler !== "function") {
            write({ ok: false, error: "Extension command has no handler: " + command.name });
            return;
          }

          const returnValue = await command.options.handler(String(payload.args ?? ""), createCommandContext(api, payload));
          const returnText = returnValue === undefined || returnValue === null ? undefined : toText(returnValue);
          write({ ok: true, actions, returnText, unsupported });
        }
        """;
}
