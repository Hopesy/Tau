// 作者：xxx
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentExtensionLifecycleEventSink
{
    /// <summary>【CodingAgent】【边界订阅】判断当前模块是否订阅指定边界</summary>
    /// <param name="type">边界事件名称</param><returns>是否存在订阅模块</returns>
    internal bool HasBoundaryHandlers(string type) => _modules.Any(module => module.EventTypes.Contains(type));

    /// <summary>【CodingAgent】【边界处理】按模块顺序传递草稿、继续标志和逐处理器重建的预览</summary>
    /// <param name="payload">初始边界事件和预览</param><param name="reportError">处理器错误接收器</param>
    /// <returns>最终事件；模块重载时丢弃整个草稿</returns>
    internal async Task<JsonElement?> EmitBoundaryEventAsync(JsonElement payload, Action<CodingAgentExtensionLifecycleEventError> reportError)
    {
        var type = payload.GetProperty("type").GetString()!;
        var generation = _runtime.ResetGeneration;
        var modules = _modules.Where(module => module.EventTypes.Contains(type)).ToArray();
        foreach (var module in modules)
        {
            if (_runtime.ResetGeneration != generation) return null;
            var result = await _runtime.EmitEventAsync(module.FilePath, payload, CancellationToken.None, generation).ConfigureAwait(false);
            if (_runtime.ResetGeneration != generation) return null;
            if (!result.Success) reportError(new(module.FilePath, module.Scope, module.Runtime, type, result.Error ?? "Boundary handler failed."));
            foreach (var error in result.HandlerErrors) reportError(new(module.FilePath, module.Scope, module.Runtime, type, error));
            if (result.TransformedEvent is { } transformed) payload = transformed;
        }
        return payload;
    }
}
