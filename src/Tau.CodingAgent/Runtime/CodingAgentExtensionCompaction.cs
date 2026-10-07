// 作者：xxx
using System.Text.Json;
using Tau.AgentCore.Harness;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentExtensionLifecycleEventSink
{
    /// <summary>【CodingAgent】【压缩代次】读取本次压缩开始时的扩展代次。</summary>
    internal long CompactionGeneration => _runtime.ResetGeneration;
    /// <summary>【CodingAgent】【压缩钩子】按模块顺序传递完整会话事件，保留最后一个返回结果并允许立即取消。</summary>
    /// <param name="payload">初始事件及准备数据。</param>
    /// <param name="reportError">扩展失败诊断接收器。</param>
    /// <param name="token">压缩取消信号。</param>
    /// <param name="generation">压缩开始时的扩展代次。</param>
    /// <returns>包含累积准备修改和最后一个处理器结果的事件。</returns>
    internal JsonElement? EmitCompactionEvent(JsonElement payload, Action<CodingAgentExtensionLifecycleEventError> reportError,
        CancellationToken token, long generation)
    {
        var type = payload.GetProperty("type").GetString()!;
        JsonElement? returned = null;
        foreach (var module in _modules.Where(module => module.EventTypes.Contains(type)))
        {
            if (generation != _runtime.ResetGeneration) break;
            token.ThrowIfCancellationRequested();
            var result = _runtime.EmitEventForGeneration(module.FilePath, payload, generation, token);
            if (!result.Success) reportError(new(module.FilePath, module.Scope, module.Runtime, type, result.Error ?? "Extension compaction event failed."));
            foreach (var error in result.HandlerErrors) reportError(new(module.FilePath, module.Scope, module.Runtime, type, error));
            if (result.TransformedEvent is not { } transformed) continue;
            payload = transformed;
            if (transformed.TryGetProperty("handlerResult", out var value) && value.ValueKind == JsonValueKind.Object) returned = value.Clone();
            if (returned is { } returnedValue && returnedValue.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True) break;
        }
        return payload;
    }
}

internal static class CodingAgentCompactionEventJson
{
    /// <summary>【CodingAgent】【准备回读】将扩展对准备数据的修改带入默认摘要生成器。</summary>
    /// <param name="payload">经过所有扩展处理的事件。</param>
    /// <param name="original">原始准备数据。</param>
    /// <returns>修改后的准备数据。</returns>
    internal static AgentCompactionPreparation ReadPreparation(JsonElement payload, AgentCompactionPreparation original)
    {
        if (!payload.TryGetProperty("preparation", out var preparation)) return original;
        var files = AgentCompaction.CreateFileOperations();
        var ops = preparation.GetProperty("fileOps");
        foreach (var path in ops.GetProperty("read").EnumerateArray()) files.Read.Add(path.GetString()!);
        foreach (var path in ops.GetProperty("written").EnumerateArray()) files.Written.Add(path.GetString()!);
        foreach (var path in ops.GetProperty("edited").EnumerateArray()) files.Edited.Add(path.GetString()!);
        var settings = preparation.GetProperty("settings");
        return new(preparation.GetProperty("firstKeptEntryId").GetString()!,
            ReadMessages(preparation.GetProperty("messagesToSummarize")), ReadMessages(preparation.GetProperty("turnPrefixMessages")),
            preparation.GetProperty("isSplitTurn").GetBoolean(), preparation.GetProperty("tokensBefore").GetInt32(),
            preparation.TryGetProperty("previousSummary", out var previous) && previous.ValueKind == JsonValueKind.String ? previous.GetString() : null,
            files, new(settings.GetProperty("enabled").GetBoolean(), settings.GetProperty("reserveTokens").GetInt32(), settings.GetProperty("keepRecentTokens").GetInt32()));
    }

    /// <summary>【CodingAgent】【摘要消息】读取扩展编辑后的标准 pi 消息数组。</summary>
    /// <param name="array">标准消息 JSON 数组。</param>
    /// <returns>可供摘要生成器使用的消息。</returns>
    private static IReadOnlyList<Tau.Ai.ChatMessage> ReadMessages(JsonElement array) => array.EnumerateArray()
        .Select(value => JsonSerializer.Deserialize(value, CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage))
        .Select(value => value is null ? null : CodingAgentSessionStore.ToMessage(value)).OfType<Tau.Ai.ChatMessage>().ToArray();

    /// <summary>【CodingAgent】【准备协议】输出上游字段名及标准消息，文件操作在 Node 中恢复为 Set。</summary>
    /// <param name="context">当前分支和原生准备结果。</param>
    /// <param name="instructions">可选摘要要求。</param>
    /// <param name="invocation">触发原因及重试信息。</param>
    /// <returns>独立的压缩前事件 JSON。</returns>
    internal static JsonElement CreateBeforeEvent(CodingAgentCompactionSessionContext context, string? instructions, CodingAgentCompactionInvocation invocation)
    {
        var preparation = context.Preparation!;
        return JsonSerializer.SerializeToElement(new
        {
            type = "session_before_compact", reason = invocation.Reason, willRetry = invocation.WillRetry, customInstructions = instructions,
            branchEntries = context.Branch.Select(entry => JsonSerializer.SerializeToElement(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)),
            preparation = new
            {
                firstKeptEntryId = preparation.FirstKeptEntryId,
                messagesToSummarize = preparation.MessagesToSummarize.Select(message => JsonSerializer.SerializeToElement(CodingAgentSessionStore.FromMessage(message), CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage)),
                turnPrefixMessages = preparation.TurnPrefixMessages.Select(message => JsonSerializer.SerializeToElement(CodingAgentSessionStore.FromMessage(message), CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage)),
                isSplitTurn = preparation.IsSplitTurn, tokensBefore = preparation.TokensBefore, previousSummary = preparation.PreviousSummary,
                fileOps = new { read = preparation.FileOperations.Read, written = preparation.FileOperations.Written, edited = preparation.FileOperations.Edited },
                settings = new { enabled = preparation.Settings.Enabled, reserveTokens = preparation.Settings.ReserveTokens, keepRecentTokens = preparation.Settings.KeepRecentTokens }
            }
        });
    }
}
