using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tau.Agent;
using Tau.Ai;

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
    bool HasPrepareArguments,
    string? ExecutionMode);

public sealed record CodingAgentJavaScriptExtensionFlag(
    string Name,
    string Description,
    string Type,
    JsonElement? DefaultValue);

public sealed record CodingAgentJavaScriptExtensionShortcut(
    string Shortcut,
    string Description,
    bool HasHandler);

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
    IReadOnlyList<CodingAgentJavaScriptExtensionFlag> Flags,
    IReadOnlyList<CodingAgentJavaScriptExtensionShortcut> Shortcuts,
    IReadOnlyList<string> EventHandlerTypes,
    CodingAgentJavaScriptExtensionUnsupportedRegistrations Unsupported,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionInvokeResult(
    bool Success,
    IReadOnlyList<string> RunnerMessages,
    string? StatusMessage,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionShortcutInvokeResult(
    bool Success,
    IReadOnlyList<string> RunnerMessages,
    string? StatusMessage,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionUiAction(
    string Method,
    string? Message,
    string? NotifyType,
    string? StatusKey,
    string? StatusText,
    string? WidgetKey,
    IReadOnlyList<string>? WidgetLines,
    string? WidgetPlacement,
    string? Title,
    string? Text);

public sealed record CodingAgentJavaScriptExtensionToolInvokeResult(
    bool Success,
    IReadOnlyList<string> Content,
    bool IsError,
    JsonElement? Details,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionToolPrepareResult(
    bool Success,
    JsonElement? PreparedArgs,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionToolCallEventResult(
    bool Success,
    bool Blocked,
    string? Reason,
    JsonElement? Arguments,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionToolResultEventResult(
    bool Success,
    IReadOnlyList<string> Content,
    bool IsError,
    JsonElement? Details,
    string? Error);

public sealed record CodingAgentJavaScriptExtensionEventEmitResult(
    bool Success,
    IReadOnlyList<string> HandlerErrors,
    string? Error);

public sealed class CodingAgentJavaScriptExtensionRuntime
{
    public const string NodeExecutableEnvironmentVariable = "TAU_CODING_AGENT_NODE";

    private const string ResultPrefix = "__TAU_EXTENSION_RESULT__";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly string _cwd;
    private readonly string? _nodeExecutable;
    private readonly TimeSpan _timeout;
    private IReadOnlyDictionary<string, object> _flagValues = EmptyFlagValues;
    private CodingAgentRpcExtensionUiBridge? _extensionUiBridge;

    private static readonly IReadOnlyDictionary<string, object> EmptyFlagValues =
        new Dictionary<string, object>(StringComparer.Ordinal);

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

    /// <summary>
    /// Seeds resolved CLI flag values that are injected into every extension invocation before the
    /// extension factory runs. Values supplied here win over <c>registerFlag</c> defaults, matching
    /// upstream <c>applyExtensionFlagValues</c>. Boolean flags use <see cref="bool"/>, string flags use
    /// <see cref="string"/>.
    /// </summary>
    public void SetFlagValues(IReadOnlyDictionary<string, object> flagValues)
    {
        _flagValues = flagValues ?? EmptyFlagValues;
    }

    public void SetExtensionUiBridge(CodingAgentRpcExtensionUiBridge? extensionUiBridge)
    {
        _extensionUiBridge = extensionUiBridge;
    }

    public CodingAgentJavaScriptExtensionLoadResult Load(string filePath)
    {
        var execution = Execute(BuildPayload("load", filePath, _cwd));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionLoadResult(false, [], [], [], [], [], EmptyUnsupported, execution.Error);
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
                    [],
                    [],
                    [],
                    ReadUnsupported(root),
                    ReadString(root, "error") ?? "javascript extension load failed");
            }

            return new CodingAgentJavaScriptExtensionLoadResult(
                true,
                ReadCommands(root),
                ReadTools(root),
                ReadFlags(root),
                ReadShortcuts(root),
                ReadStringArray(root, "eventHandlers"),
                ReadUnsupported(root),
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionLoadResult(
                false,
                [],
                [],
                [],
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

            DispatchUiActions(root);
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

    public CodingAgentJavaScriptExtensionShortcutInvokeResult InvokeShortcut(
        string filePath,
        string shortcut)
    {
        var execution = Execute(BuildPayload("invokeShortcut", filePath, _cwd, shortcut: shortcut));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionShortcutInvokeResult(false, [], null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionShortcutInvokeResult(
                    false,
                    [],
                    null,
                    ReadString(root, "error") ?? "javascript extension shortcut failed");
            }

            DispatchUiActions(root);
            return new CodingAgentJavaScriptExtensionShortcutInvokeResult(
                true,
                ReadRunnerMessages(root),
                ReadString(root, "returnText"),
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionShortcutInvokeResult(
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

            DispatchUiActions(root);
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

    public CodingAgentJavaScriptExtensionToolPrepareResult PrepareToolArguments(
        string filePath,
        string toolName,
        JsonElement args)
    {
        var execution = Execute(BuildPayload(
            "prepareToolArguments",
            filePath,
            _cwd,
            toolName: toolName,
            toolArgs: args));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionToolPrepareResult(false, null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionToolPrepareResult(
                    false,
                    null,
                    ReadString(root, "error") ?? "javascript extension tool argument preparation failed");
            }

            if (!root.TryGetProperty("preparedArgs", out var preparedArgs))
            {
                return new CodingAgentJavaScriptExtensionToolPrepareResult(
                    false,
                    null,
                    "javascript extension tool did not return prepared arguments");
            }

            return new CodingAgentJavaScriptExtensionToolPrepareResult(
                true,
                preparedArgs.Clone(),
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionToolPrepareResult(
                false,
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    public CodingAgentJavaScriptExtensionToolCallEventResult EmitToolCall(
        string filePath,
        string toolName,
        string toolCallId,
        JsonElement args)
    {
        var execution = Execute(BuildPayload(
            "emitToolCall",
            filePath,
            _cwd,
            toolName: toolName,
            toolCallId: toolCallId,
            toolArgs: args));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionToolCallEventResult(false, false, null, null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionToolCallEventResult(
                    false,
                    false,
                    null,
                    null,
                    ReadString(root, "error") ?? "javascript extension tool_call handler failed");
            }

            DispatchUiActions(root);
            return new CodingAgentJavaScriptExtensionToolCallEventResult(
                true,
                ReadBool(root, "block"),
                ReadString(root, "reason"),
                root.TryGetProperty("input", out var inputElement) ? inputElement.Clone() : null,
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionToolCallEventResult(
                false,
                false,
                null,
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    public CodingAgentJavaScriptExtensionToolResultEventResult EmitToolResult(
        string filePath,
        string toolName,
        string toolCallId,
        JsonElement args,
        ToolResult result)
    {
        var execution = Execute(BuildPayload(
            "emitToolResult",
            filePath,
            _cwd,
            toolName: toolName,
            toolCallId: toolCallId,
            toolArgs: args,
            toolResult: result));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionToolResultEventResult(false, [], result.IsError, null, execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionToolResultEventResult(
                    false,
                    [],
                    result.IsError,
                    null,
                    ReadString(root, "error") ?? "javascript extension tool_result handler failed");
            }

            DispatchUiActions(root);
            return new CodingAgentJavaScriptExtensionToolResultEventResult(
                true,
                ReadStringArray(root, "content"),
                ReadBool(root, "isError"),
                root.TryGetProperty("details", out var details) ? details.Clone() : null,
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionToolResultEventResult(
                false,
                [],
                result.IsError,
                null,
                $"invalid node extension runtime output: {ex.Message}");
        }
    }

    public CodingAgentJavaScriptExtensionEventEmitResult EmitEvent(
        string filePath,
        JsonElement extensionEvent)
    {
        var execution = Execute(BuildPayload(
            "emitEvent",
            filePath,
            _cwd,
            extensionEvent: extensionEvent));
        if (!execution.Success)
        {
            return new CodingAgentJavaScriptExtensionEventEmitResult(false, [], execution.Error);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (!ReadBool(root, "ok"))
            {
                return new CodingAgentJavaScriptExtensionEventEmitResult(
                    false,
                    [],
                    ReadString(root, "error") ?? "javascript extension event handler failed");
            }

            DispatchUiActions(root);
            return new CodingAgentJavaScriptExtensionEventEmitResult(
                true,
                ReadStringArray(root, "handlerErrors"),
                null);
        }
        catch (JsonException ex)
        {
            return new CodingAgentJavaScriptExtensionEventEmitResult(
                false,
                [],
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

    private string BuildPayload(
        string mode,
        string filePath,
        string cwd,
        string? commandName = null,
        string? args = null,
        string? shortcut = null,
        string? toolName = null,
        string? toolCallId = null,
        JsonElement? toolArgs = null,
        ToolResult? toolResult = null,
        JsonElement? extensionEvent = null)
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

            if (shortcut is not null)
            {
                writer.WriteString("shortcut", shortcut);
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

            if (toolResult is not null)
            {
                writer.WritePropertyName("toolResult");
                WriteToolResult(writer, toolResult);
            }

            if (extensionEvent.HasValue)
            {
                writer.WritePropertyName("event");
                extensionEvent.Value.WriteTo(writer);
            }

            if (_flagValues.Count > 0)
            {
                writer.WritePropertyName("flagValues");
                writer.WriteStartObject();
                foreach (var (name, value) in _flagValues)
                {
                    switch (value)
                    {
                        case bool boolValue:
                            writer.WriteBoolean(name, boolValue);
                            break;
                        case string stringValue:
                            writer.WriteString(name, stringValue);
                            break;
                    }
                }

                writer.WriteEndObject();
            }

            if (_extensionUiBridge is not null)
            {
                writer.WriteBoolean("hasExtensionUi", true);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteToolResult(Utf8JsonWriter writer, ToolResult result)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("isError", result.IsError);
        writer.WritePropertyName("content");
        WriteContentBlocks(writer, result.Content);
        if (result.Details is not null)
        {
            writer.WritePropertyName("details");
            WriteObject(writer, result.Details);
        }

        writer.WriteEndObject();
    }

    private static void WriteContentBlocks(Utf8JsonWriter writer, IReadOnlyList<ContentBlock> content)
    {
        writer.WriteStartArray();
        foreach (var block in content)
        {
            writer.WriteStartObject();
            switch (block)
            {
                case TextContent text:
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    break;
                case ImageContent image:
                    writer.WriteString("type", "image");
                    writer.WriteString("data", image.Data);
                    writer.WriteString("mimeType", image.MimeType);
                    break;
                case ThinkingContent thinking:
                    writer.WriteString("type", "text");
                    writer.WriteString("text", thinking.Thinking);
                    break;
                default:
                    writer.WriteString("type", block.Type);
                    break;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteObject(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case JsonDocument document:
                document.RootElement.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int integer:
                writer.WriteNumberValue(integer);
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
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
                ReadBool(tool, "hasPrepareArguments"),
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

    private static IReadOnlyList<CodingAgentJavaScriptExtensionFlag> ReadFlags(JsonElement root)
    {
        if (!root.TryGetProperty("flags", out var flagsElement) ||
            flagsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var flags = new List<CodingAgentJavaScriptExtensionFlag>();
        foreach (var flag in flagsElement.EnumerateArray())
        {
            if (flag.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(flag, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            flags.Add(new CodingAgentJavaScriptExtensionFlag(
                name,
                ReadString(flag, "description") ?? string.Empty,
                ReadString(flag, "type") ?? string.Empty,
                ReadFlagDefault(flag)));
        }

        return flags.ToArray();
    }

    private static IReadOnlyList<CodingAgentJavaScriptExtensionShortcut> ReadShortcuts(JsonElement root)
    {
        if (!root.TryGetProperty("shortcuts", out var shortcutsElement) ||
            shortcutsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var shortcuts = new List<CodingAgentJavaScriptExtensionShortcut>();
        foreach (var shortcut in shortcutsElement.EnumerateArray())
        {
            if (shortcut.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var key = ReadString(shortcut, "shortcut");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            shortcuts.Add(new CodingAgentJavaScriptExtensionShortcut(
                key,
                ReadString(shortcut, "description") ?? string.Empty,
                ReadBool(shortcut, "hasHandler")));
        }

        return shortcuts.ToArray();
    }

    private static JsonElement? ReadFlagDefault(JsonElement flag)
    {
        if (!flag.TryGetProperty("default", out var value))
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.String
            ? value.Clone()
            : null;
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

    private void DispatchUiActions(JsonElement root)
    {
        var bridge = _extensionUiBridge;
        if (bridge is null)
        {
            return;
        }

        foreach (var action in ReadUiActions(root))
        {
            switch (action.Method)
            {
                case "notify":
                    if (!string.IsNullOrWhiteSpace(action.Message))
                    {
                        bridge.NotifyAsync(action.Message, action.NotifyType, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    break;
                case "setStatus":
                    if (!string.IsNullOrWhiteSpace(action.StatusKey))
                    {
                        bridge.SetStatusAsync(action.StatusKey, action.StatusText, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    break;
                case "setWidget":
                    if (!string.IsNullOrWhiteSpace(action.WidgetKey))
                    {
                        bridge.SetWidgetAsync(action.WidgetKey, action.WidgetLines, action.WidgetPlacement, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    break;
                case "setTitle":
                    if (!string.IsNullOrWhiteSpace(action.Title))
                    {
                        bridge.SetTitleAsync(action.Title, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    break;
                case "set_editor_text":
                    bridge.SetEditorTextAsync(action.Text ?? string.Empty, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    break;
            }
        }
    }

    private static IReadOnlyList<CodingAgentJavaScriptExtensionUiAction> ReadUiActions(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actionsElement) ||
            actionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var actions = new List<CodingAgentJavaScriptExtensionUiAction>();
        foreach (var action in actionsElement.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(action, "type"), "ui", StringComparison.Ordinal))
            {
                continue;
            }

            var method = ReadString(action, "method");
            if (string.IsNullOrWhiteSpace(method))
            {
                continue;
            }

            actions.Add(new CodingAgentJavaScriptExtensionUiAction(
                method,
                ReadString(action, "message"),
                ReadString(action, "notifyType"),
                ReadString(action, "statusKey"),
                ReadString(action, "statusText"),
                ReadString(action, "widgetKey"),
                ReadOptionalStringArray(action, "widgetLines"),
                ReadString(action, "widgetPlacement"),
                ReadString(action, "title"),
                ReadString(action, "text")));
        }

        return actions.ToArray();
    }

    private static IReadOnlyList<string>? ReadOptionalStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (arrayElement.ValueKind != JsonValueKind.Array)
        {
            return null;
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
        let extensionImportHookInstalled = false;

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

        function mergeSchemaOptions(schema, options) {
          const result = { ...schema };
          if (options && typeof options === "object") Object.assign(result, options);
          return result;
        }

        const typeBoxModuleSource = String.raw`
        function mergeSchemaOptions(schema, options) {
          const result = { ...schema };
          if (options && typeof options === "object") Object.assign(result, options);
          return result;
        }
        function markOptional(schema) {
          const result = { ...(schema || {}) };
          Object.defineProperty(result, "__tauOptional", { value: true, enumerable: false });
          return result;
        }
        function stripOptional(schema) {
          const result = { ...(schema || {}) };
          return result;
        }
        function literalType(value) {
          if (value === null) return "null";
          if (Array.isArray(value)) return "array";
          return typeof value;
        }
        export const Type = {
          Any(options = {}) { return mergeSchemaOptions({}, options); },
          Unknown(options = {}) { return mergeSchemaOptions({}, options); },
          Null(options = {}) { return mergeSchemaOptions({ type: "null" }, options); },
          String(options = {}) { return mergeSchemaOptions({ type: "string" }, options); },
          Number(options = {}) { return mergeSchemaOptions({ type: "number" }, options); },
          Integer(options = {}) { return mergeSchemaOptions({ type: "integer" }, options); },
          Boolean(options = {}) { return mergeSchemaOptions({ type: "boolean" }, options); },
          Literal(value, options = {}) { return mergeSchemaOptions({ const: value, enum: [value], type: literalType(value) }, options); },
          Array(items = {}, options = {}) { return mergeSchemaOptions({ type: "array", items }, options); },
          Union(items = [], options = {}) { return mergeSchemaOptions({ anyOf: items }, options); },
          Optional(schema = {}) { return markOptional(schema); },
          Record(_keySchema = {}, valueSchema = {}, options = {}) {
            return mergeSchemaOptions({ type: "object", additionalProperties: stripOptional(valueSchema) }, options);
          },
          Object(properties = {}, options = {}) {
            const normalized = {};
            const required = [];
            for (const [key, value] of Object.entries(properties || {})) {
              const propertySchema = stripOptional(value);
              normalized[key] = propertySchema;
              if (!value || value.__tauOptional !== true) required.push(key);
            }
            const schema = { type: "object", properties: normalized };
            if (required.length > 0) schema.required = required;
            return mergeSchemaOptions(schema, options);
          }
        };
        export default { Type };
        `;

        const piAiModuleSource = typeBoxModuleSource + String.raw`
        const apiProviders = new Map();
        const modelRegistry = new Map();
        const oauthRegistryKey = Symbol.for("@tau/pi-ai/oauth-registry");
        const oauthProviders = globalThis[oauthRegistryKey] ??= new Map();
        export function registerApiProvider(provider, sourceId) {
          if (provider && provider.api) apiProviders.set(provider.api, { provider, sourceId });
        }
        export function getApiProvider(api) { return apiProviders.get(api)?.provider; }
        export function getApiProviders() { return Array.from(apiProviders.values(), entry => entry.provider); }
        export function unregisterApiProviders(sourceId) {
          for (const [api, entry] of apiProviders.entries()) if (entry.sourceId === sourceId) apiProviders.delete(api);
        }
        export function clearApiProviders() { apiProviders.clear(); }
        export function registerModel(provider, model) {
          if (!modelRegistry.has(provider)) modelRegistry.set(provider, new Map());
          modelRegistry.get(provider).set(model.id, { ...model, provider });
        }
        export function getModel(provider, modelId) { return modelRegistry.get(provider)?.get(modelId); }
        export function getProviders() { return Array.from(modelRegistry.keys()); }
        export function getModels(provider) { return Array.from(modelRegistry.get(provider)?.values() ?? []); }
        export function calculateCost(model, usage) {
          const cost = usage.cost ?? {};
          const rates = model?.cost ?? {};
          cost.input = ((rates.input ?? 0) / 1000000) * (usage.input ?? 0);
          cost.output = ((rates.output ?? 0) / 1000000) * (usage.output ?? 0);
          cost.cacheRead = ((rates.cacheRead ?? 0) / 1000000) * (usage.cacheRead ?? 0);
          cost.cacheWrite = ((rates.cacheWrite ?? 0) / 1000000) * (usage.cacheWrite ?? 0);
          cost.total = cost.input + cost.output + cost.cacheRead + cost.cacheWrite;
          usage.cost = cost;
          return cost;
        }
        export function supportsXhigh(model) {
          const id = String(model?.id ?? "");
          return id.includes("gpt-5.2") || id.includes("gpt-5.3") || id.includes("gpt-5.4") ||
            id.includes("opus-4-6") || id.includes("opus-4.6") ||
            id.includes("opus-4-7") || id.includes("opus-4.7");
        }
        export function modelsAreEqual(a, b) { return !!a && !!b && a.id === b.id && a.provider === b.provider; }
        export function getOAuthProvider(id) { return oauthProviders.get(id); }
        export function registerOAuthProvider(provider) { if (provider && provider.id) oauthProviders.set(provider.id, provider); }
        export function unregisterOAuthProvider(id) { oauthProviders.delete(id); }
        export function resetOAuthProviders() { oauthProviders.clear(); }
        export function getOAuthProviders() { return Array.from(oauthProviders.values()); }
        export function getOAuthProviderInfoList() {
          return getOAuthProviders().map(provider => ({ id: provider.id, name: provider.name, available: true }));
        }
        export async function refreshOAuthToken(providerId, credentials) {
          const provider = getOAuthProvider(providerId);
          if (!provider || typeof provider.refreshToken !== "function") throw new Error("Unknown OAuth provider: " + providerId);
          return provider.refreshToken(credentials);
        }
        export async function getOAuthApiKey(providerId, credentials) {
          const provider = getOAuthProvider(providerId);
          const credential = credentials ? credentials[providerId] : undefined;
          if (!provider || !credential) return null;
          if (typeof provider.getApiKey !== "function") return null;
          return { newCredentials: credential, apiKey: provider.getApiKey(credential) };
        }
        `;

        const piAiOAuthModuleSource = String.raw`
        const oauthRegistryKey = Symbol.for("@tau/pi-ai/oauth-registry");
        const providers = globalThis[oauthRegistryKey] ??= new Map();
        export function getOAuthProvider(id) { return providers.get(id); }
        export function registerOAuthProvider(provider) { if (provider && provider.id) providers.set(provider.id, provider); }
        export function unregisterOAuthProvider(id) { providers.delete(id); }
        export function resetOAuthProviders() { providers.clear(); }
        export function getOAuthProviders() { return Array.from(providers.values()); }
        export function getOAuthProviderInfoList() {
          return getOAuthProviders().map(provider => ({ id: provider.id, name: provider.name, available: true }));
        }
        export async function refreshOAuthToken(providerId, credentials) {
          const provider = getOAuthProvider(providerId);
          if (!provider || typeof provider.refreshToken !== "function") throw new Error("Unknown OAuth provider: " + providerId);
          return provider.refreshToken(credentials);
        }
        export async function getOAuthApiKey(providerId, credentials) {
          const provider = getOAuthProvider(providerId);
          const credential = credentials ? credentials[providerId] : undefined;
          if (!provider || !credential) return null;
          if (typeof provider.getApiKey !== "function") return null;
          return { newCredentials: credential, apiKey: provider.getApiKey(credential) };
        }
        `;

        const piAgentCoreModuleSource = String.raw`
        export const ToolExecutionMode = Object.freeze({ Sequential: "sequential", Parallel: "parallel" });
        export class EventStream {
          constructor() { this.events = []; }
          push(event) { this.events.push(event); }
          async *[Symbol.asyncIterator]() { for (const event of this.events) yield event; }
        }
        export class Agent {
          constructor(options = {}) { this.options = options; }
        }
        `;

        const piTuiModuleSource = String.raw`
        export class Container {
          constructor() { this.children = []; }
          addChild(child) { this.children.push(child); return child; }
          clear() { this.children = []; }
          render(width = 80) { return this.children.flatMap(child => typeof child?.render === "function" ? child.render(width) : []); }
        }
        export class Text {
          constructor(text = "", paddingX = 1, paddingY = 1) { this.text = String(text ?? ""); this.paddingX = paddingX; this.paddingY = paddingY; }
          setText(text) { this.text = String(text ?? ""); }
          render() { return this.text.trim().length === 0 ? [] : [this.text]; }
        }
        export class Spacer { constructor(lines = 1) { this.lines = lines; } render() { return Array.from({ length: Math.max(0, this.lines) }, () => ""); } }
        export class Box extends Container {}
        export class Markdown extends Text {}
        export class TruncatedText extends Text {}
        export class Input extends Text {}
        export class Loader extends Text {}
        export class CancellableLoader extends Loader {}
        export class SelectList extends Container {}
        export class SettingsList extends Container {}
        export class Image extends Text {}
        export class TUI {}
        export class ProcessTerminal {}
        export class KeybindingsManager { matches() { return false; } }
        export const CURSOR_MARKER = "";
        export const TUI_KEYBINDINGS = {};
        export function getKeybindings() { return new KeybindingsManager(); }
        export function setKeybindings() {}
        export function matchesKey(actual, expected) { return actual === expected; }
        export function parseKey(value) { return String(value ?? ""); }
        export function visibleWidth(value) { return String(value ?? "").replace(/\x1b\[[0-9;]*m/g, "").length; }
        export function truncateToWidth(value, width) { return String(value ?? "").slice(0, Math.max(0, width)); }
        export function wrapTextWithAnsi(value) { return [String(value ?? "")]; }
        export function fuzzyMatch(query, value) { return String(value ?? "").toLowerCase().includes(String(query ?? "").toLowerCase()) ? { score: 1 } : undefined; }
        export function fuzzyFilter(items, query) { return (items ?? []).filter(item => fuzzyMatch(query, String(item))); }
        export function getCapabilities() { return {}; }
        export function getImageDimensions() { return undefined; }
        export function imageFallback() { return ""; }
        `;

        const piCodingAgentModuleSource = String.raw`
        export function defineTool(tool) { return tool; }
        export function createEventBus() {
          const listeners = new Map();
          return {
            on(type, handler) {
              const list = listeners.get(type) ?? [];
              list.push(handler);
              listeners.set(type, list);
              return () => listeners.set(type, (listeners.get(type) ?? []).filter(candidate => candidate !== handler));
            },
            async emit(type, payload) {
              for (const handler of listeners.get(type) ?? []) await handler(payload);
            }
          };
        }
        export class ModelRegistry {}
        export class SessionManager {}
        export class CustomEditor {
          constructor(...args) { this.args = args; this.actionHandlers = new Map(); }
          onAction(action, handler) { this.actionHandlers.set(action, handler); }
          handleInput() {}
          getText() { return ""; }
          isShowingAutocomplete() { return false; }
          render() { return []; }
          dispose() {}
        }
        export function createSyntheticSourceInfo(path, options = {}) { return { path, ...options }; }
        `;

        const virtualModuleSources = new Map([
          ["@sinclair/typebox", typeBoxModuleSource],
          ["@mariozechner/pi-agent-core", piAgentCoreModuleSource],
          ["@mariozechner/pi-tui", piTuiModuleSource],
          ["@mariozechner/pi-ai", piAiModuleSource],
          ["@mariozechner/pi-ai/oauth", piAiOAuthModuleSource],
          ["@mariozechner/pi-coding-agent", piCodingAgentModuleSource]
        ]);

        function virtualModuleUrl(specifier) {
          const source = virtualModuleSources.get(specifier);
          return source === undefined
            ? undefined
            : "data:text/javascript;charset=utf-8," + encodeURIComponent(source);
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

        function installExtensionImportHook(requireHooks) {
          if (extensionImportHookInstalled) return;
          if (typeof moduleApi.registerHooks !== "function") {
            if (requireHooks) {
              throw new Error("typescript extension runtime unavailable: Node.js module hooks are not available");
            }
            return;
          }

          moduleApi.registerHooks({
            resolve(specifier, context, nextResolve) {
              const url = virtualModuleUrl(specifier);
              if (url) {
                return { url, shortCircuit: true };
              }
              return nextResolve(specifier, context);
            },
            load(url, context, nextLoad) {
              const parsed = new URL(url);
              if (parsed.protocol === "file:" && parsed.pathname.toLowerCase().endsWith(".ts")) {
                if (typeof moduleApi.stripTypeScriptTypes !== "function") {
                  throw new Error("typescript extension runtime unavailable: Node.js type stripping hooks are not available");
                }
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
          extensionImportHookInstalled = true;
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

        function normalizeToolEventContent(value) {
          if (!Array.isArray(value)) return normalizeToolContent(value);
          return value.map(item => toText(item)).filter(text => text.length > 0);
        }

        function normalizeContentBlocks(value) {
          if (!Array.isArray(value)) return [];
          return value.map(item => {
            if (item && typeof item === "object") return item;
            return { type: "text", text: toText(item) };
          });
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

        function normalizeStringArray(value) {
          if (value === undefined || value === null) return undefined;
          if (!Array.isArray(value)) return undefined;
          return value.map(item => toText(item));
        }

        const supportedEventNames = new Set([
          "tool_call",
          "tool_result",
          "agent_start",
          "agent_end",
          "turn_start",
          "turn_end",
          "message_start",
          "message_update",
          "message_end",
          "tool_execution_start",
          "tool_execution_update",
          "tool_execution_end"
        ]);

        function addHandler(handlerMap, unsupported, eventName, handler) {
          const key = String(eventName ?? "");
          if (!supportedEventNames.has(key) || typeof handler !== "function") {
            unsupported.handlers++;
            return () => {};
          }

          const handlers = handlerMap.get(key) ?? [];
          handlers.push(handler);
          handlerMap.set(key, handlers);
          return () => {
            const current = handlerMap.get(key) ?? [];
            const index = current.indexOf(handler);
            if (index >= 0) current.splice(index, 1);
          };
        }

        function createApi(commandMap, toolMap, flagMap, shortcutMap, flagValues, handlerMap, unsupported, actions, payload) {
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
            registerFlag(name, options = {}) {
              const key = String(name ?? "");
              const flagOptions = options || {};
              flagMap.set(key, { name: key, options: flagOptions });
              if (Object.prototype.hasOwnProperty.call(flagOptions, "default") && !flagValues.has(key)) {
                const defaultValue = flagOptions.default;
                if (typeof defaultValue === "boolean" || typeof defaultValue === "string") {
                  flagValues.set(key, defaultValue);
                }
              }
            },
            registerShortcut(shortcut, options = {}) {
              const key = String(shortcut ?? "");
              shortcutMap.set(key, { shortcut: key, options: options || {} });
            },
            registerMessageRenderer() { unsupported.messageRenderers++; },
            registerProvider() { unsupported.providers++; },
            unregisterProvider() { unsupported.providers++; },
            on(eventName, handler) { return addHandler(handlerMap, unsupported, eventName, handler); },
            getFlag(name) {
              const key = String(name ?? "");
              if (!flagMap.has(key)) return undefined;
              return flagValues.get(key);
            },
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
              on(eventName, handler) { return addHandler(handlerMap, unsupported, eventName, handler); },
              emit() {}
            },
            cwd: payload.cwd
          };
        }

        function createUiContext(actions) {
          const addUiAction = (method, fields = {}) => {
            actions.push({ type: "ui", method, ...fields });
          };
          return {
            select: async () => undefined,
            confirm: async () => false,
            input: async () => undefined,
            notify: (message, type) => addUiAction("notify", {
              message: toText(message),
              notifyType: typeof type === "string" ? type : undefined
            }),
            onTerminalInput: () => () => {},
            setStatus: (key, text) => addUiAction("setStatus", {
              statusKey: String(key ?? ""),
              statusText: text === undefined ? undefined : toText(text)
            }),
            setWorkingMessage: () => {},
            setHiddenThinkingLabel: () => {},
            setWidget: (key, content, options = {}) => {
              const widgetLines = normalizeStringArray(content);
              if (content === undefined || widgetLines !== undefined) {
                addUiAction("setWidget", {
                  widgetKey: String(key ?? ""),
                  widgetLines,
                  widgetPlacement: typeof options?.placement === "string" ? options.placement : undefined
                });
              }
            },
            setFooter: () => {},
            setHeader: () => {},
            setTitle: title => addUiAction("setTitle", { title: toText(title) }),
            custom: async () => undefined,
            pasteToEditor(text) { this.setEditorText(text); },
            setEditorText: text => addUiAction("set_editor_text", { text: toText(text) }),
            getEditorText: () => "",
            editor: async () => undefined
          };
        }

        function createCommandContext(api, payload, actions) {
          return {
            ui: createUiContext(actions),
            hasUI: payload.hasExtensionUi === true,
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
          installExtensionImportHook(isTypeScriptFile(filePath));
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
          const flagMap = new Map();
          const shortcutMap = new Map();
          const flagValues = new Map();
          if (payload.flagValues && typeof payload.flagValues === "object") {
            for (const key of Object.keys(payload.flagValues)) {
              const value = payload.flagValues[key];
              if (typeof value === "boolean" || typeof value === "string") {
                flagValues.set(key, value);
              }
            }
          }
          const handlerMap = new Map();
          const unsupported = { tools: 0, flags: 0, shortcuts: 0, handlers: 0, messageRenderers: 0, providers: 0 };
          const actions = [];
          const api = createApi(commandMap, toolMap, flagMap, shortcutMap, flagValues, handlerMap, unsupported, actions, payload);
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
            hasPrepareArguments: typeof tool?.prepareArguments === "function",
            executionMode: typeof tool?.executionMode === "string" ? tool.executionMode : undefined
          }));

          const flags = Array.from(flagMap.values()).map(flag => {
            const defaultValue = flag.options && Object.prototype.hasOwnProperty.call(flag.options, "default")
              ? flag.options.default
              : undefined;
            return {
              name: flag.name,
              description: typeof flag.options?.description === "string" ? flag.options.description : "",
              type: flag.options?.type === "boolean" || flag.options?.type === "string" ? flag.options.type : "",
              default: typeof defaultValue === "boolean" || typeof defaultValue === "string" ? defaultValue : undefined
            };
          });

          const shortcuts = Array.from(shortcutMap.values()).map(shortcut => ({
            shortcut: shortcut.shortcut,
            description: typeof shortcut.options?.description === "string" ? shortcut.options.description : "",
            hasHandler: typeof shortcut.options?.handler === "function"
          }));

          const eventHandlers = Array.from(handlerMap.entries())
            .filter(entry => entry[1].length > 0)
            .map(entry => entry[0]);

          if (payload.mode === "load") {
            write({ ok: true, commands, tools, flags, shortcuts, eventHandlers, unsupported });
            return;
          }

          if (payload.mode === "invokeShortcut") {
            const shortcut = shortcutMap.get(String(payload.shortcut ?? ""));
            if (!shortcut) {
              write({ ok: false, error: "Extension shortcut was not registered: " + String(payload.shortcut ?? "") });
              return;
            }
            if (typeof shortcut.options.handler !== "function") {
              write({ ok: false, error: "Extension shortcut has no handler: " + shortcut.shortcut });
              return;
            }

            const returnValue = await shortcut.options.handler(createCommandContext(api, payload, actions));
            const returnText = returnValue === undefined || returnValue === null ? undefined : toText(returnValue);
            write({ ok: true, actions, returnText, unsupported });
            return;
          }

          if (payload.mode === "emitToolCall") {
            const handlers = handlerMap.get("tool_call") ?? [];
            const event = {
              type: "tool_call",
              toolName: String(payload.toolName ?? ""),
              toolCallId: String(payload.toolCallId ?? ""),
              input: payload.toolArgs && typeof payload.toolArgs === "object" ? payload.toolArgs : {}
            };

            let result = undefined;
            for (const handler of handlers) {
              const handlerResult = await handler(event, createCommandContext(api, payload, actions));
              if (handlerResult) {
                result = handlerResult;
                if (result.block) break;
              }
            }

            write({
              ok: true,
              block: result && result.block === true,
              reason: result && typeof result.reason === "string" ? result.reason : undefined,
              input: event.input,
              actions
            });
            return;
          }

          if (payload.mode === "emitToolResult") {
            const handlers = handlerMap.get("tool_result") ?? [];
            const event = {
              type: "tool_result",
              toolName: String(payload.toolName ?? ""),
              toolCallId: String(payload.toolCallId ?? ""),
              input: payload.toolArgs && typeof payload.toolArgs === "object" ? payload.toolArgs : {},
              content: normalizeContentBlocks(payload.toolResult?.content),
              details: payload.toolResult && Object.prototype.hasOwnProperty.call(payload.toolResult, "details")
                ? payload.toolResult.details
                : undefined,
              isError: payload.toolResult?.isError === true
            };

            for (const handler of handlers) {
              try {
                const handlerResult = await handler(event, createCommandContext(api, payload, actions));
                if (!handlerResult) continue;
                if (Object.prototype.hasOwnProperty.call(handlerResult, "content")) {
                  event.content = normalizeContentBlocks(handlerResult.content);
                }
                if (Object.prototype.hasOwnProperty.call(handlerResult, "details")) {
                  event.details = handlerResult.details;
                }
                if (Object.prototype.hasOwnProperty.call(handlerResult, "isError")) {
                  event.isError = handlerResult.isError === true;
                }
              } catch {
              }
            }

            write({
              ok: true,
              content: normalizeToolEventContent(event.content),
              isError: event.isError,
              details: event.details,
              actions
            });
            return;
          }

          if (payload.mode === "emitEvent") {
            const event = payload.event && typeof payload.event === "object" ? payload.event : { type: "" };
            const eventType = String(event.type ?? "");
            const handlers = handlerMap.get(eventType) ?? [];
            const handlerErrors = [];
            for (const handler of handlers) {
              try {
                await handler(event, createCommandContext(api, payload, actions));
              } catch (error) {
                handlerErrors.push(formatError(error));
              }
            }

            write({ ok: true, handlerErrors, actions, unsupported });
            return;
          }

          if (payload.mode === "prepareToolArguments") {
            const tool = toolMap.get(String(payload.toolName ?? ""));
            if (!tool) {
              write({ ok: false, error: "Extension tool was not registered: " + String(payload.toolName ?? "") });
              return;
            }

            if (typeof tool.prepareArguments !== "function") {
              write({ ok: true, preparedArgs: payload.toolArgs === undefined ? {} : payload.toolArgs });
              return;
            }

            const preparedArgs = await tool.prepareArguments(payload.toolArgs === undefined ? {} : payload.toolArgs);
            write({ ok: true, preparedArgs: preparedArgs === undefined ? {} : preparedArgs });
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
              createCommandContext(api, payload, actions));
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

          const returnValue = await command.options.handler(String(payload.args ?? ""), createCommandContext(api, payload, actions));
          const returnText = returnValue === undefined || returnValue === null ? undefined : toText(returnValue);
          write({ ok: true, actions, returnText, unsupported });
        }
        """;
}
