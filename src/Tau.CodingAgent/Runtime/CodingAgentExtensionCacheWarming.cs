// 作者：xxx
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentExtensionLifecycleEventSink
{
    /// <summary>【CodingAgent】【预热扩展】所有处理器接收原始决策，最后一个有效返回值覆盖动作。</summary>
    /// <param name="decision">默认计费决策。</param><param name="reportError">扩展错误接收器。</param>
    /// <returns>warm 或 stop；重载和异常保留默认决定。</returns>
    internal async Task<string> EmitCacheWarmingDecisionAsync(CodingAgentCacheWarmingDecision decision,
        Action<CodingAgentExtensionLifecycleEventError> reportError)
    {
        const string type = "cache_warming_decision";
        var payload = JsonSerializer.SerializeToElement(new { type, warmCost = decision.WarmCost, missCost = decision.MissCost,
            continuationProbability = decision.ContinuationProbability, action = decision.Action });
        var action = decision.Action;
        var generation = _runtime.ResetGeneration;
        foreach (var module in _modules.Where(module => module.EventTypes.Contains(type)).ToArray())
        {
            if (_runtime.ResetGeneration != generation) return decision.Action;
            var result = await _runtime.EmitEventAsync(module.FilePath, payload, CancellationToken.None, generation).ConfigureAwait(false);
            if (_runtime.ResetGeneration != generation) return decision.Action;
            if (!result.Success) reportError(new(module.FilePath, module.Scope, module.Runtime, type, result.Error ?? "Cache warming handler failed."));
            foreach (var error in result.HandlerErrors) reportError(new(module.FilePath, module.Scope, module.Runtime, type, error));
            if (result.TransformedEvent is { } transformed && transformed.TryGetProperty("overrideAction", out var selected)
                && selected.ValueKind == JsonValueKind.String && selected.GetString() is "warm" or "stop") action = selected.GetString()!;
        }
        return action;
    }
}
