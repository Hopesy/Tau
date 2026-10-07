// 作者：xxx
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentExtensionCommandStore
{
    internal CodingAgentTreeSessionController? CurrentTreeSessionController => _javaScriptRuntime.TreeController;
    /// <summary>【CodingAgent】【替换钩子】按扩展加载顺序分发可取消事件，并立即停止已取消的处理器链。</summary>
    /// <param name="payload">切换或分叉前事件。</param>
    /// <param name="runner">接收扩展错误诊断的运行器。</param>
    /// <param name="token">命令取消信号。</param>
    /// <returns>扩展是否取消操作。</returns>
    internal async Task<bool> PublishBeforeSessionReplacementAsync(JsonElement payload, RuntimeCodingAgentRunner runner, CancellationToken token)
    {
        var result = await PublishMutableSessionEventAsync(payload, runner, token).ConfigureAwait(false);
        return result.TryGetProperty("handlerResult", out var value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True;
    }

    /// <summary>【CodingAgent】【可变事件】顺序传递导航准备和处理结果，错误隔离，取消立即停止。</summary>
    /// <param name="payload">会话事件及准备数据。</param>
    /// <param name="runner">诊断接收器。</param>
    /// <param name="token">取消信号。</param>
    /// <returns>最后的事件与处理结果。</returns>
    internal async Task<JsonElement> PublishMutableSessionEventAsync(JsonElement payload, RuntimeCodingAgentRunner runner, CancellationToken token)
    {
        var type = payload.GetProperty("type").GetString()!;
        var generation = _javaScriptRuntime.ResetGeneration;
        var modules = LoadStatus().EventHandlers.Where(handler => handler.EventType == type)
            .GroupBy(handler => Path.GetFullPath(handler.FilePath), StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        var errors = new List<CodingAgentExtensionLifecycleEventError>();
        foreach (var module in modules)
        {
            token.ThrowIfCancellationRequested();
            // 1. 【CodingAgent】【可取消会话钩子】异步等待跨进程处理，允许宿主在准备期间中断导航
            var result = await _javaScriptRuntime.EmitEventAsync(module.FilePath, payload, token, generation).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!result.Success) errors.Add(new(module.FilePath, module.Scope, module.Runtime, type, result.Error ?? "Extension session hook failed."));
            foreach (var error in result.HandlerErrors) errors.Add(new(module.FilePath, module.Scope, module.Runtime, type, error));
            if (result.TransformedEvent is { } transformed) payload = transformed;
            if (payload.TryGetProperty("handlerResult", out var value)
                && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True)
            {
                runner.ReportSessionHookErrors(errors);
                return payload;
            }
        }
        runner.ReportSessionHookErrors(errors);
        return payload;
    }
}
