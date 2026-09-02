using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace Tau.CodingAgent.Runtime;

/// <summary>
/// 校验 pi protocol 的 client/server CBOR envelope 与嵌套消息结构。
/// </summary>
internal static class ProtocolMessages
{
    private const int ProtocolVersion = 1;

    /// <summary>校验 client 消息。</summary>
    /// <param name="value">待校验的消息。</param>
    public static void ValidateClientMessage(object? value) => Validate(value, "client");

    /// <summary>校验 server 消息。</summary>
    /// <param name="value">待校验的消息。</param>
    public static void ValidateServerMessage(object? value) => Validate(value, "server");

    /// <summary>按消息方向校验 envelope。</summary>
    /// <param name="value">待校验对象。</param>
    /// <param name="kind">消息方向，取 client 或 server。</param>
    public static void Validate(object? value, string kind)
    {
        var map = RequireMap(value, $"Invalid {kind} protocol message");
        var type = RequireString(map, "type", "message type");
        if (kind.Equals("client", StringComparison.Ordinal))
        {
            switch (type)
            {
                case "hello": ValidateClientHello(map); break;
                case "request": ValidateRequestEnvelope(map); break;
                default: throw Invalid($"Invalid client protocol message type '{type}'");
            }
            return;
        }

        if (!kind.Equals("server", StringComparison.Ordinal)) throw Invalid($"Unknown protocol message kind '{kind}'");
        switch (type)
        {
            case "hello": ValidateServerHello(map); break;
            case "hello_error": ValidateHelloError(map); break;
            case "response": ValidateResponseEnvelope(map); break;
            case "event": ValidateEventEnvelope(map); break;
            default: throw Invalid($"Invalid server protocol message type '{type}'");
        }
    }

    /// <summary>校验 client hello。</summary>
    private static void ValidateClientHello(IReadOnlyDictionary<string, object?> map)
    {
        ExactKeys(map, ["type", "version"]);
        RequireInteger(map, "version", minimum: 0);
    }

    /// <summary>校验请求 envelope 和命令参数。</summary>
    private static void ValidateRequestEnvelope(IReadOnlyDictionary<string, object?> map)
    {
        ExactKeys(map, ["type", "id", "request"]);
        RequireString(map, "id", "request id");
        ValidateCommand(RequireMap(map["request"], "request command"));
    }

    /// <summary>校验 client 命令的严格字段集合。</summary>
    private static void ValidateCommand(IReadOnlyDictionary<string, object?> command)
    {
        var name = RequireString(command, "command", "command name");
        switch (name)
        {
            case "list":
                ExactKeys(command, ["command"]);
                break;
            case "create":
                ExactKeys(command, ["command", "cwd", "name", "model", "thinkingLevel"], optional: ["cwd", "name", "model", "thinkingLevel"]);
                OptionalNonEmptyString(command, "cwd");
                OptionalString(command, "name");
                if (command.TryGetValue("model", out var createModel)) ValidateModelRef(RequireMap(createModel, "create model"));
                OptionalThinkingLevel(command, "thinkingLevel");
                break;
            case "attach":
            case "detach":
                ExactKeys(command, ["command", "sessionId"]);
                RequireString(command, "sessionId", "session id");
                break;
            case "prompt":
            case "steer":
                ExactKeys(command, ["command", "sessionId", "text"]);
                RequireString(command, "sessionId", "session id");
                RequireString(command, "text", "prompt text", allowEmpty: true);
                break;
            case "abort":
                ExactKeys(command, ["command", "sessionId"]);
                RequireString(command, "sessionId", "session id");
                break;
            case "set_model":
                ExactKeys(command, ["command", "sessionId", "model"]);
                RequireString(command, "sessionId", "session id");
                ValidateModelRef(RequireMap(command["model"], "model"));
                break;
            case "set_thinking":
                ExactKeys(command, ["command", "sessionId", "thinkingLevel"]);
                RequireString(command, "sessionId", "session id");
                RequireThinkingLevel(command, "thinkingLevel");
                break;
            default:
                throw Invalid($"Invalid protocol command '{name}'");
        }
    }

    /// <summary>校验 server hello。</summary>
    private static void ValidateServerHello(IReadOnlyDictionary<string, object?> map)
    {
        ExactKeys(map, ["type", "version", "connectionId", "snapshot"]);
        if (RequireInteger(map, "version", minimum: 0) != ProtocolVersion) throw Invalid("Unsupported protocol version");
        RequireString(map, "connectionId", "connection id");
        ValidateServerSnapshot(RequireMap(map["snapshot"], "server snapshot"));
    }

    /// <summary>校验 server hello_error。</summary>
    private static void ValidateHelloError(IReadOnlyDictionary<string, object?> map)
    {
        ExactKeys(map, ["type", "error"]);
        ValidateProtocolError(RequireMap(map["error"], "hello error"));
    }

    /// <summary>校验 response envelope 的成功和失败分支。</summary>
    private static void ValidateResponseEnvelope(IReadOnlyDictionary<string, object?> map)
    {
        ExactKeys(map, ["type", "id", "ok", "result", "error"] , optional: ["result", "error"]);
        RequireString(map, "id", "response id");
        var ok = RequireBoolean(map, "ok");
        if (ok)
        {
            if (!map.TryGetValue("result", out var result)) throw Invalid("Successful response requires result");
            ValidateCommandResult(RequireMap(result, "response result"));
            if (map.ContainsKey("error")) throw Invalid("Successful response cannot contain error");
        }
        else
        {
            if (!map.TryGetValue("error", out var error)) throw Invalid("Failed response requires error");
            ValidateProtocolError(RequireMap(error, "response error"));
            if (map.ContainsKey("result")) throw Invalid("Failed response cannot contain result");
        }
    }

    /// <summary>校验 server event envelope。</summary>
    private static void ValidateEventEnvelope(IReadOnlyDictionary<string, object?> map)
    {
        ExactKeys(map, ["type", "event"]);
        var value = RequireMap(map["event"], "server event");
        switch (RequireString(value, "type", "event type"))
        {
            case "server_snapshot":
                ExactKeys(value, ["type", "snapshot"]);
                ValidateServerSnapshot(RequireMap(value["snapshot"], "server snapshot"));
                break;
            case "session_snapshot":
                ExactKeys(value, ["type", "snapshot"]);
                ValidateSessionSnapshot(RequireMap(value["snapshot"], "session snapshot"));
                break;
            case "session_progress":
                ExactKeys(value, ["type", "sessionId", "progress"]);
                RequireString(value, "sessionId", "session id");
                ValidateTranscriptProgress(RequireMap(value["progress"], "transcript progress"));
                break;
            case "session_removed":
                ExactKeys(value, ["type", "sessionId"]);
                RequireString(value, "sessionId", "session id");
                break;
            default:
                throw Invalid("Invalid server event type");
        }
    }

    /// <summary>校验通用 command result。</summary>
    private static void ValidateCommandResult(IReadOnlyDictionary<string, object?> result)
    {
        var command = RequireString(result, "command", "result command");
        switch (command)
        {
            case "list":
                ExactKeys(result, ["command", "sessions"]);
                foreach (var item in RequireArray(result, "sessions")) ValidateSessionMetadata(RequireMap(item, "session metadata"));
                break;
            case "detach":
                ExactKeys(result, ["command", "sessionId"]);
                RequireString(result, "sessionId", "session id");
                break;
            case "create":
            case "attach":
            case "prompt":
            case "steer":
            case "abort":
            case "set_model":
            case "set_thinking":
                ExactKeys(result, ["command", "session"]);
                ValidateSessionSnapshot(RequireMap(result["session"], "session snapshot"));
                break;
            default:
                throw Invalid($"Invalid command result '{command}'");
        }
    }

    /// <summary>校验 server snapshot。</summary>
    private static void ValidateServerSnapshot(IReadOnlyDictionary<string, object?> snapshot)
    {
        ExactKeys(snapshot, ["serverId", "protocolVersion", "revision", "sessions", "models"]);
        RequireString(snapshot, "serverId", "server id");
        if (RequireInteger(snapshot, "protocolVersion", minimum: 0) != ProtocolVersion) throw Invalid("Unsupported snapshot protocol version");
        RequireInteger(snapshot, "revision", minimum: 0);
        foreach (var item in RequireArray(snapshot, "sessions")) ValidateSessionMetadata(RequireMap(item, "session metadata"));
        foreach (var item in RequireArray(snapshot, "models")) ValidateModelMetadata(RequireMap(item, "model metadata"));
    }

    /// <summary>校验 session metadata。</summary>
    private static void ValidateSessionMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        ExactKeys(metadata, ["id", "createdAt", "updatedAt", "parentSessionId", "sessionName", "cwd"], optional: ["updatedAt", "parentSessionId", "sessionName", "cwd"]);
        RequireString(metadata, "id", "session id");
        RequireInteger(metadata, "createdAt", minimum: 0);
        OptionalInteger(metadata, "updatedAt", minimum: 0);
        OptionalString(metadata, "parentSessionId");
        OptionalString(metadata, "sessionName");
        OptionalNonEmptyString(metadata, "cwd");
    }

    /// <summary>校验完整 session snapshot。</summary>
    private static void ValidateSessionSnapshot(IReadOnlyDictionary<string, object?> snapshot)
    {
        ExactKeys(snapshot, ["id", "name", "cwd", "createdAt", "updatedAt", "phase", "model", "thinkingLevel", "attached", "locked", "revision", "transcript", "queuedSteer", "queuedSteerCount"], optional: ["name"]);
        RequireString(snapshot, "id", "session id");
        OptionalString(snapshot, "name");
        RequireString(snapshot, "cwd", "working directory");
        RequireInteger(snapshot, "createdAt", minimum: 0);
        RequireInteger(snapshot, "updatedAt", minimum: 0);
        RequireStringIn(snapshot, "phase", ["idle", "turn", "compaction", "branch_summary", "retry"]);
        ValidateModelRef(RequireMap(snapshot["model"], "session model"));
        RequireThinkingLevel(snapshot, "thinkingLevel");
        RequireBoolean(snapshot, "attached");
        RequireBoolean(snapshot, "locked");
        RequireInteger(snapshot, "revision", minimum: 0);
        foreach (var item in RequireArray(snapshot, "transcript")) ValidateTranscriptItem(RequireMap(item, "transcript item"));
        foreach (var item in RequireArray(snapshot, "queuedSteer")) ValidateUserTranscriptItem(RequireMap(item, "queued steer item"));
        RequireInteger(snapshot, "queuedSteerCount", minimum: 0);
    }

    /// <summary>校验模型引用。</summary>
    private static void ValidateModelRef(IReadOnlyDictionary<string, object?> model)
    {
        ExactKeys(model, ["provider", "id"]);
        RequireString(model, "provider", "model provider");
        RequireString(model, "id", "model id");
    }

    /// <summary>校验公开模型元数据。</summary>
    private static void ValidateModelMetadata(IReadOnlyDictionary<string, object?> model)
    {
        ExactKeys(model, ["provider", "id", "name", "api", "reasoning", "input", "contextWindow", "maxTokens", "cost", "supportedThinkingLevels", "authenticated"]);
        RequireString(model, "provider", "model provider");
        RequireString(model, "id", "model id");
        RequireString(model, "name", "model name");
        RequireString(model, "api", "model api");
        RequireBoolean(model, "reasoning");
        foreach (var item in RequireArray(model, "input"))
            if (item is not string modality || modality is not ("text" or "image")) throw Invalid("Invalid model input modality");
        RequireInteger(model, "contextWindow", minimum: 1);
        RequireInteger(model, "maxTokens", minimum: 1);
        ValidateModelCost(RequireMap(model["cost"], "model cost"));
        foreach (var item in RequireArray(model, "supportedThinkingLevels"))
            if (item is not string level || !IsThinkingLevel(level)) throw Invalid("Invalid supported thinking level");
        RequireBoolean(model, "authenticated");
    }

    /// <summary>校验模型费用。</summary>
    private static void ValidateModelCost(IReadOnlyDictionary<string, object?> cost)
    {
        ExactKeys(cost, ["input", "output", "cacheRead", "cacheWrite"]);
        RequireNumber(cost, "input", minimum: 0);
        RequireNumber(cost, "output", minimum: 0);
        RequireNumber(cost, "cacheRead", minimum: 0);
        RequireNumber(cost, "cacheWrite", minimum: 0);
    }

    /// <summary>校验协议错误。</summary>
    private static void ValidateProtocolError(IReadOnlyDictionary<string, object?> error)
    {
        ExactKeys(error, ["code", "message", "details"], optional: ["details"]);
        RequireStringIn(error, "code", ["version", "busy", "session_locked", "not_found", "invalid_request", "not_implemented", "internal_error"]);
        RequireString(error, "message", "error message", allowEmpty: true);
        if (error.TryGetValue("details", out var details)) ValidateJsonValue(details);
    }

    /// <summary>校验 transcript progress。</summary>
    private static void ValidateTranscriptProgress(IReadOnlyDictionary<string, object?> progress)
    {
        var type = RequireString(progress, "type", "progress type");
        switch (type)
        {
            case "item_started":
                ExactKeys(progress, ["type", "item"]);
                ValidateTranscriptItem(RequireMap(progress["item"], "started item"));
                break;
            case "assistant_delta":
                ExactKeys(progress, ["type", "messageId", "contentIndex", "kind", "delta"]);
                RequireString(progress, "messageId", "message id");
                RequireInteger(progress, "contentIndex", minimum: 0);
                RequireStringIn(progress, "kind", ["text", "thinking", "toolCall"]);
                RequireString(progress, "delta", "assistant delta", allowEmpty: true);
                break;
            case "item_updated":
                ExactKeys(progress, ["type", "item"]);
                var updated = RequireMap(progress["item"], "updated item");
                if (RequireString(updated, "role", "updated role") is not ("assistant" or "tool")) throw Invalid("Invalid updated transcript role");
                ValidateTranscriptItem(updated);
                break;
            case "item_finished":
                ExactKeys(progress, ["type", "item"]);
                var finished = RequireMap(progress["item"], "finished item");
                ValidateTranscriptItem(finished);
                if (RequireString(finished, "status", "finished status") is not ("complete" or "error" or "aborted")) throw Invalid("Invalid finished transcript status");
                break;
            default: throw Invalid("Invalid transcript progress type");
        }
    }

    /// <summary>校验一个 transcript item。</summary>
    private static void ValidateTranscriptItem(IReadOnlyDictionary<string, object?> item)
    {
        switch (RequireString(item, "role", "transcript role"))
        {
            case "user": ValidateUserTranscriptItem(item); break;
            case "assistant": ValidateAssistantTranscriptItem(item); break;
            case "tool": ValidateToolTranscriptItem(item); break;
            default: throw Invalid("Invalid transcript role");
        }
    }

    /// <summary>校验 user transcript item。</summary>
    private static void ValidateUserTranscriptItem(IReadOnlyDictionary<string, object?> item)
    {
        ExactKeys(item, ["id", "role", "content", "timestamp"]);
        RequireString(item, "id", "user item id");
        if (!string.Equals(RequireString(item, "role", "user role"), "user", StringComparison.Ordinal)) throw Invalid("Invalid user role");
        foreach (var content in RequireArray(item, "content")) ValidateContent(RequireMap(content, "user content"), assistant: false, tool: false);
        RequireInteger(item, "timestamp", minimum: 0);
    }

    /// <summary>校验 assistant transcript item。</summary>
    private static void ValidateAssistantTranscriptItem(IReadOnlyDictionary<string, object?> item)
    {
        ExactKeys(item, ["id", "role", "content", "model", "responseModel", "usage", "timestamp", "status", "stopReason", "errorMessage"], optional: ["responseModel", "usage", "stopReason", "errorMessage"]);
        RequireString(item, "id", "assistant item id");
        if (!string.Equals(RequireString(item, "role", "assistant role"), "assistant", StringComparison.Ordinal)) throw Invalid("Invalid assistant role");
        foreach (var content in RequireArray(item, "content")) ValidateContent(RequireMap(content, "assistant content"), assistant: true, tool: false);
        ValidateModelRef(RequireMap(item["model"], "assistant model"));
        OptionalString(item, "responseModel");
        if (item.TryGetValue("usage", out var usage)) ValidateUsage(RequireMap(usage, "assistant usage"));
        RequireInteger(item, "timestamp", minimum: 0);
        var status = RequireString(item, "status", "assistant status");
        if (status is not ("streaming" or "complete" or "error" or "aborted")) throw Invalid("Invalid assistant status");
        if (status == "complete" && RequireStringIn(item, "stopReason", ["stop", "length", "toolUse"]) is null) throw Invalid("Complete assistant item requires stop reason");
        if (status == "error" && RequireString(item, "stopReason", "error stop reason") != "error") throw Invalid("Error assistant item requires error stop reason");
        if (status == "aborted" && RequireString(item, "stopReason", "aborted stop reason") != "aborted") throw Invalid("Aborted assistant item requires aborted stop reason");
        OptionalString(item, "errorMessage");
    }

    /// <summary>校验 tool transcript item。</summary>
    private static void ValidateToolTranscriptItem(IReadOnlyDictionary<string, object?> item)
    {
        ExactKeys(item, ["id", "role", "toolCallId", "toolName", "input", "content", "details", "usage", "timestamp", "status", "isError"], optional: ["details", "usage"]);
        RequireString(item, "id", "tool item id");
        if (RequireString(item, "role", "tool role") != "tool") throw Invalid("Invalid tool role");
        RequireString(item, "toolCallId", "tool call id");
        RequireString(item, "toolName", "tool name");
        ValidateJsonValue(item["input"]);
        foreach (var content in RequireArray(item, "content")) ValidateContent(RequireMap(content, "tool content"), assistant: false, tool: true);
        if (item.TryGetValue("details", out var details)) ValidateJsonValue(details);
        if (item.TryGetValue("usage", out var usage)) ValidateUsage(RequireMap(usage, "tool usage"));
        RequireInteger(item, "timestamp", minimum: 0);
        var status = RequireString(item, "status", "tool status");
        if (status is not ("running" or "complete" or "error")) throw Invalid("Invalid tool status");
        if (RequireBoolean(item, "isError") != (status == "error")) throw Invalid("Tool error status disagrees with isError");
    }

    /// <summary>校验 transcript content block。</summary>
    private static void ValidateContent(IReadOnlyDictionary<string, object?> content, bool assistant, bool tool)
    {
        var type = RequireString(content, "type", "content type");
        switch (type)
        {
            case "text":
                ExactKeys(content, ["type", "text"]);
                RequireString(content, "text", "text content", allowEmpty: true);
                break;
            case "thinking" when assistant:
                ExactKeys(content, ["type", "thinking", "redacted"], optional: ["redacted"]);
                RequireString(content, "thinking", "thinking content", allowEmpty: true);
                if (content.TryGetValue("redacted", out var redacted) && redacted is not bool) throw Invalid("Invalid thinking redacted flag");
                break;
            case "toolCall" when assistant:
                ExactKeys(content, ["type", "toolCallId", "toolName", "input"]);
                RequireString(content, "toolCallId", "tool call id");
                RequireString(content, "toolName", "tool name");
                ValidateJsonValue(content["input"]);
                break;
            case "image" when !assistant:
                ExactKeys(content, ["type", "data", "mimeType"]);
                RequireString(content, "data", "image data", allowEmpty: true);
                RequireString(content, "mimeType", "image mime type");
                break;
            default:
                throw Invalid("Invalid content block for transcript role");
        }
    }

    /// <summary>校验用量结构。</summary>
    private static void ValidateUsage(IReadOnlyDictionary<string, object?> usage)
    {
        ExactKeys(usage, ["input", "output", "cacheRead", "cacheWrite", "reasoning", "totalTokens", "cost"], optional: ["reasoning"]);
        RequireInteger(usage, "input", minimum: 0);
        RequireInteger(usage, "output", minimum: 0);
        RequireInteger(usage, "cacheRead", minimum: 0);
        RequireInteger(usage, "cacheWrite", minimum: 0);
        OptionalInteger(usage, "reasoning", minimum: 0);
        RequireInteger(usage, "totalTokens", minimum: 0);
        var cost = RequireMap(usage["cost"], "usage cost");
        ExactKeys(cost, ["input", "output", "cacheRead", "cacheWrite", "total"]);
        RequireNumber(cost, "input", minimum: 0);
        RequireNumber(cost, "output", minimum: 0);
        RequireNumber(cost, "cacheRead", minimum: 0);
        RequireNumber(cost, "cacheWrite", minimum: 0);
        RequireNumber(cost, "total", minimum: 0);
    }

    /// <summary>校验 JSON-compatible 值。</summary>
    private static void ValidateJsonValue(object? value)
    {
        if (value is null or bool or string) return;
        if (value is byte[] || value is DateTime || value is DateTimeOffset || value is Guid) throw Invalid("Invalid JSON value");
        if (value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            if (value is double number && !double.IsFinite(number) || value is float single && !float.IsFinite(single)) throw Invalid("Invalid JSON number");
            return;
        }
        if (value is IEnumerable enumerable && value is not string && value is not IDictionary)
        {
            foreach (var item in enumerable) ValidateJsonValue(item);
            return;
        }
        var map = RequireMap(value, "JSON object");
        foreach (var pair in map) ValidateJsonValue(pair.Value);
    }

    /// <summary>将对象视为 protocol map，支持解码字典和发送侧匿名对象。</summary>
    private static IReadOnlyDictionary<string, object?> RequireMap(object? value, string description)
    {
        if (value is IReadOnlyDictionary<string, object?> readOnly) return readOnly;
        if (value is IDictionary<string, object?> generic) return new Dictionary<string, object?>(generic, StringComparer.Ordinal);
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key || !result.TryAdd(key, entry.Value)) throw Invalid("Protocol map keys must be unique strings");
            }
            return result;
        }
        if (value is null || value is string || value.GetType().IsPrimitive) throw Invalid(description);
        var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToDictionary(property => property.Name, property => property.GetValue(value), StringComparer.Ordinal);
        return properties;
    }

    /// <summary>校验字段集合并拒绝未知字段。</summary>
    private static void ExactKeys(IReadOnlyDictionary<string, object?> map, IReadOnlySet<string> required, IReadOnlySet<string>? optional = null)
    {
        foreach (var key in required) if (!map.ContainsKey(key)) throw Invalid($"Protocol message is missing '{key}'");
        foreach (var key in map.Keys)
            if (!required.Contains(key) && !(optional?.Contains(key) ?? false)) throw Invalid($"Protocol message contains unknown field '{key}'");
    }

    /// <summary>重载字段集合校验，便于调用方提供必选和可选数组。</summary>
    private static void ExactKeys(IReadOnlyDictionary<string, object?> map, string[] required, string[]? optional = null) =>
        ExactKeys(map, required.ToHashSet(StringComparer.Ordinal), optional?.ToHashSet(StringComparer.Ordinal));

    /// <summary>读取非空字符串字段。</summary>
    private static string RequireString(IReadOnlyDictionary<string, object?> map, string name, string description, bool allowEmpty = false)
    {
        if (!map.TryGetValue(name, out var value) || value is not string text || (!allowEmpty && text.Length == 0)) throw Invalid($"Invalid {description}");
        return text;
    }

    /// <summary>读取可选字符串字段。</summary>
    private static void OptionalString(IReadOnlyDictionary<string, object?> map, string name)
    {
        if (map.TryGetValue(name, out var value) && value is not string) throw Invalid($"Invalid '{name}'");
    }

    /// <summary>读取可选非空字符串字段。</summary>
    private static void OptionalNonEmptyString(IReadOnlyDictionary<string, object?> map, string name)
    {
        if (map.TryGetValue(name, out var value) && (value is not string text || text.Length == 0)) throw Invalid($"Invalid '{name}'");
    }

    /// <summary>读取整数并校验下限。</summary>
    private static long RequireInteger(IReadOnlyDictionary<string, object?> map, string name, long minimum)
    {
        if (!map.TryGetValue(name, out var value)) throw Invalid($"Protocol message is missing '{name}'");
        long number = value switch { byte b => b, sbyte b => b, short s => s, ushort s => s, int i => i, uint i => i, long l => l, _ => long.MinValue };
        if (number < minimum) throw Invalid($"Invalid integer field '{name}'");
        return number;
    }

    /// <summary>读取可选整数。</summary>
    private static void OptionalInteger(IReadOnlyDictionary<string, object?> map, string name, long minimum)
    {
        if (map.ContainsKey(name)) RequireInteger(map, name, minimum);
    }

    /// <summary>读取有限数值并校验下限。</summary>
    private static double RequireNumber(IReadOnlyDictionary<string, object?> map, string name, double minimum)
    {
        if (!map.TryGetValue(name, out var value)) throw Invalid($"Protocol message is missing '{name}'");
        var number = value switch { byte b => b, sbyte b => b, short s => s, ushort s => s, int i => i, uint i => i, long l => l, ulong l => l, float f => f, double d => d, decimal d => (double)d, _ => double.NaN };
        if (!double.IsFinite(number) || number < minimum) throw Invalid($"Invalid number field '{name}'");
        return number;
    }

    /// <summary>读取布尔字段。</summary>
    private static bool RequireBoolean(IReadOnlyDictionary<string, object?> map, string name)
    {
        if (!map.TryGetValue(name, out var value) || value is not bool result) throw Invalid($"Invalid boolean field '{name}'");
        return result;
    }

    /// <summary>读取限定字符串字段。</summary>
    private static string RequireStringIn(IReadOnlyDictionary<string, object?> map, string name, params string[] allowed)
    {
        var value = RequireString(map, name, name);
        if (!allowed.Contains(value, StringComparer.Ordinal)) throw Invalid($"Invalid value '{value}' for '{name}'");
        return value;
    }

    /// <summary>读取数组字段。</summary>
    private static IReadOnlyList<object?> RequireArray(IReadOnlyDictionary<string, object?> map, string name)
    {
        if (!map.TryGetValue(name, out var value) || value is string || value is not IEnumerable enumerable) throw Invalid($"Invalid array field '{name}'");
        return enumerable.Cast<object?>().ToArray();
    }

    /// <summary>读取并校验思考级别。</summary>
    private static void RequireThinkingLevel(IReadOnlyDictionary<string, object?> map, string name) =>
        _ = RequireStringIn(map, name, ["off", "minimal", "low", "medium", "high", "xhigh", "max"]);

    /// <summary>校验可选思考级别。</summary>
    private static void OptionalThinkingLevel(IReadOnlyDictionary<string, object?> map, string name)
    {
        if (map.ContainsKey(name)) RequireThinkingLevel(map, name);
    }

    /// <summary>判断思考级别是否受协议支持。</summary>
    private static bool IsThinkingLevel(string value) => value is "off" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max";

    /// <summary>创建统一协议校验异常。</summary>
    private static ProtocolValidationException Invalid(string message) => new(message);
}
