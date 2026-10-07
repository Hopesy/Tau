// 作者：xxx
using System.Collections.Concurrent;
using System.Text.Json;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【原生压缩】按真实分支边界运行压缩钩子、摘要生成和一次性提交。</summary>
    /// <param name="instructions">可选摘要要求。</param>
    /// <param name="invocation">当前压缩原因、代次和来源。</param>
    /// <param name="token">压缩取消信号。</param>
    /// <returns>已包含持久化标识的摘要结果。</returns>
    private async Task<CodingAgentCompactionResult> CompactPreparedSessionAsync(string? instructions, CodingAgentCompactionInvocation invocation, CancellationToken token)
    {
        var context = _readCompactionContext!();
        var preparation = context.Preparation ?? throw new InvalidOperationException(
            context.Branch.LastOrDefault()?.Type == "compaction" ? "Already compacted" : "Nothing to compact (session too small)");
        var beforeCount = Messages.Count;
        var transformed = EmitCompactionHook(CodingAgentCompactionEventJson.CreateBeforeEvent(context, instructions, invocation), token, invocation.Generation);
        var returned = transformed is { } evt && evt.TryGetProperty("handlerResult", out var answer) ? answer : (JsonElement?)null;
        if (transformed is { } edited) preparation = CodingAgentCompactionEventJson.ReadPreparation(edited, preparation);
        if (returned is { } decision && decision.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True)
            throw new OperationCanceledException("Compaction cancelled.");
        CodingAgentCompactionResult result;
        // 1. 【CodingAgent】【摘要选择】扩展可提供完整结果，未提供时使用共享的历史与拆分回合生成器
        if (returned is { } hook && hook.TryGetProperty("compaction", out var provided) && provided.ValueKind == JsonValueKind.Object)
        {
            invocation.FromExtension = true;
            var summary = provided.GetProperty("summary").GetString();
            var first = provided.GetProperty("firstKeptEntryId").GetString();
            var tokens = provided.GetProperty("tokensBefore").GetInt32();
            if (string.IsNullOrWhiteSpace(summary) || !context.Branch.Any(entry => entry.Id == first) || tokens < 0)
                throw new InvalidOperationException("Invalid extension compaction result.");
            result = new(summary, beforeCount, 0, tokens, first, true)
            {
                Details = provided.TryGetProperty("details", out var details) ? details.Clone() : null,
                Usage = provided.TryGetProperty("usage", out var usage) ? usage.Clone() : null,
                UsesPreparedBoundary = true
            };
        }
        else
        {
            var responses = new ConcurrentQueue<Usage>();
            var options = (await CreateSummaryGenerationOptionsAsync(instructions, false, 4096, token).ConfigureAwait(false)) with
            {
                MaxTokens = null, ReserveTokens = preparation.Settings.ReserveTokens,
                RetryCallbacks = CreateSummaryRetryCallbacks("compaction", invocation.Reason),
                OnSummaryResponse = message => { if (message.Usage is { } usage) responses.Enqueue(usage); }
            };
            var generated = await AgentCompactionSummaries.CompactAsync(preparation, options).ConfigureAwait(false);
            result = new(generated.Summary, beforeCount, 0, generated.TokensBefore, generated.FirstKeptEntryId)
            {
                Details = JsonSerializer.SerializeToElement(new { readFiles = generated.Details.ReadFiles, modifiedFiles = generated.Details.ModifiedFiles }),
                Usage = SerializeSummaryUsage(responses), UsesPreparedBoundary = true
            };
        }
        // 2. 【CodingAgent】【原子提交】只有生成完成且未取消时才提交，通知中的条目来自最终会话
        token.ThrowIfCancellationRequested();
        result = _commitPreparedCompaction!(result);
        var saved = _readCompactionEntry!(result.StoredEntryId!);
        EmitCompactionHook(JsonSerializer.SerializeToElement(new
        {
            type = "session_compact", reason = invocation.Reason, willRetry = invocation.WillRetry, fromExtension = invocation.FromExtension,
            compactionEntry = JsonSerializer.SerializeToElement(saved, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)
        }), CancellationToken.None, invocation.Generation);
        return result;

    }

    /// <summary>【CodingAgent】【压缩事件】分发扩展钩子并复用已有诊断日志。</summary>
    /// <param name="payload">事件对象。</param>
    /// <param name="token">压缩取消信号。</param>
    /// <param name="generation">开始压缩时的扩展代次。</param>
    /// <returns>取消或自定义摘要结果。</returns>
    private JsonElement? EmitCompactionHook(JsonElement payload, CancellationToken token, long generation) => _extensionLifecycleEventSink?.EmitCompactionEvent(
        payload, error => LogExtensionEventError(error, CreateRunLogContext()), token, generation);

    /// <summary>【CodingAgent】【摘要用量】合并历史与拆分前缀两次请求的标准 token 与费用字段。</summary>
    /// <param name="values">所有已完成摘要响应的用量。</param>
    /// <returns>标准 pi 用量对象；没有用量时为空。</returns>
    private static JsonElement? SerializeSummaryUsage(IEnumerable<Usage> values)
    {
        var items = values.ToArray();
        if (items.Length == 0) return null;
        return JsonSerializer.SerializeToElement(new
        {
            input = items.Sum(item => item.InputTokens), output = items.Sum(item => item.OutputTokens),
            cacheRead = items.Sum(item => item.CacheReadTokens ?? 0), cacheWrite = items.Sum(item => item.CacheWriteTokens ?? 0),
            totalTokens = items.Sum(item => item.TotalTokens ?? item.InputTokens + item.OutputTokens + (item.CacheReadTokens ?? 0) + (item.CacheWriteTokens ?? 0)),
            cost = new
            {
                input = items.Sum(item => item.Cost?.Input ?? 0), output = items.Sum(item => item.Cost?.Output ?? 0),
                cacheRead = items.Sum(item => item.Cost?.CacheRead ?? 0), cacheWrite = items.Sum(item => item.Cost?.CacheWrite ?? 0),
                total = items.Sum(item => item.Cost?.Total ?? 0)
            }
        });
    }
}
