// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【用户命令钩子】扩展可以提供已完成结果或自定义执行后端。</summary>
/// <param name="Result">扩展直接返回的完整结果。</param><param name="Operations">扩展执行后端。</param>
internal sealed record CodingAgentUserBashHookResult(CodingAgentShellResult? Result = null, ICodingAgentShellOperations? Operations = null) : IAsyncDisposable
{
    /// <summary>【CodingAgent】【命令后端释放】释放未消费的跨进程执行能力。</summary><returns>释放任务。</returns>
    public ValueTask DisposeAsync() => Operations is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}

public sealed partial class CodingAgentExtensionCommandStore
{
    /// <summary>【CodingAgent】【用户命令事件】按扩展顺序选择首个接管命令的处理器，错误时停止并禁止本地回退执行。</summary>
    /// <param name="command">原始命令。</param><param name="excluded">是否排除模型上下文。</param><param name="runner">当前运行器。</param>
    /// <param name="token">直接命令取消信号。</param><returns>扩展接管结果；空值表示继续本地执行。</returns>
    internal async Task<CodingAgentUserBashHookResult?> EmitUserBashAsync(string command, bool excluded, RuntimeCodingAgentRunner runner, CancellationToken token)
    {
        var generation = _javaScriptRuntime.ResetGeneration;
        var modules = LoadStatus().EventHandlers.Where(handler => handler.EventType == "user_bash")
            .GroupBy(handler => Path.GetFullPath(handler.FilePath), StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        foreach (var module in modules)
        {
            token.ThrowIfCancellationRequested();
            // 1. 【CodingAgent】【后端归属】宿主预分配标识，取消或异常时仍能释放未返回的能力
            var id = Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.SerializeToElement(new { type = "user_bash", command, excludeFromContext = excluded, cwd = runner.WorkingDirectory, _tauOperationId = id });
            try
            {
                var emitted = await _javaScriptRuntime.EmitEventAsync(module.FilePath, payload, token, generation).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!emitted.Success || emitted.HandlerErrors.Count > 0)
                {
                    var error = emitted.Error ?? string.Join("\n", emitted.HandlerErrors);
                    runner.ReportSessionHookErrors([new(module.FilePath, module.Scope, module.Runtime, "user_bash", error)]);
                    throw new InvalidOperationException(error);
                }
                if (emitted.TransformedEvent is not { } transformed || !transformed.TryGetProperty("handlerResult", out var result)) continue;
                if (result.TryGetProperty("result", out var completed))
                    return new(new(completed.GetProperty("output").GetString()!,
                        completed.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number ? exitCode.GetInt32() : null,
                        completed.GetProperty("cancelled").GetBoolean(), completed.GetProperty("truncated").GetBoolean(),
                        completed.TryGetProperty("fullOutputPath", out var path) ? path.GetString() : null));
                if (result.TryGetProperty("operationId", out var operationId))
                    return new(Operations: new UserBashOperations(_javaScriptRuntime, module.FilePath, operationId.GetString()!, generation));
            }
            catch
            {
                await _javaScriptRuntime.ReleaseUserBashAsync(module.FilePath, id, generation).ConfigureAwait(false);
                throw;
            }
        }
        return null;
    }

    /// <summary>【CodingAgent】【扩展命令后端】将远程操作能力绑定到产生它的扩展代次。</summary>
    /// <param name="runtime">Node 运行时。</param><param name="path">扩展路径。</param><param name="id">一次性后端标识。</param><param name="generation">扩展代次。</param>
    private sealed class UserBashOperations(CodingAgentJavaScriptExtensionRuntime runtime, string path, string id, long generation) : ICodingAgentShellOperations, IAsyncDisposable
    {
        /// <summary>【CodingAgent】【扩展命令调用】转发命令、工作目录、输出及取消信号。</summary>
        /// <param name="context">实际执行上下文。</param><param name="onData">输出回调。</param><param name="token">取消信号。</param><returns>退出码。</returns>
        public Task<int?> ExecuteAsync(CodingAgentShellSpawnContext context, Func<string, Task> onData, CancellationToken token) =>
            runtime.ExecuteUserBashAsync(path, id, generation, context, onData, token);

        /// <summary>【CodingAgent】【后端释放】清除未执行或提前取消的 Node 能力，重复释放无副作用。</summary><returns>释放任务。</returns>
        public ValueTask DisposeAsync() => new(runtime.ReleaseUserBashAsync(path, id, generation));
    }
}

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【Node 后端释放】删除扩展代次内的未消费命令能力，重载后的旧能力已经失效。</summary>
    /// <param name="path">扩展路径。</param><param name="id">能力标识。</param><param name="generation">创建代次。</param><returns>释放任务。</returns>
    internal async Task ReleaseUserBashAsync(string path, string id, long generation)
    {
        using var arguments = JsonDocument.Parse(new JsonObject { ["operationId"] = id }.ToJsonString());
        await ExecuteAsync(BuildPayload("releaseUserBash", path, _cwd, toolArgs: arguments.RootElement), expectedGeneration: generation).ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【Node 命令后端】调用扩展提供的 Shell 操作，实时排空输出并遵守命令取消，执行时长不受普通脚本时限约束。</summary>
    /// <param name="path">扩展路径。</param><param name="id">后端标识。</param><param name="generation">产生标识时的代次。</param>
    /// <param name="context">命令上下文。</param><param name="onData">增量输出接收器。</param><param name="token">取消信号。</param><returns>退出码。</returns>
    internal async Task<int?> ExecuteUserBashAsync(string path, string id, long generation, CodingAgentShellSpawnContext context,
        Func<string, Task> onData, CancellationToken token)
    {
        using var arguments = JsonDocument.Parse(new JsonObject { ["operationId"] = id, ["command"] = context.Command, ["cwd"] = context.Cwd }.ToJsonString());
        var execution = await ExecuteAsync(BuildPayload("executeUserBash", path, _cwd, toolArgs: arguments.RootElement), token,
            update => onData(update.GetProperty("text").GetString() ?? ""), generation, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!execution.Success) throw new InvalidOperationException(execution.Error);
        using var document = JsonDocument.Parse(execution.ResultJson);
        var root = document.RootElement;
        if (!ReadBool(root, "ok")) throw new InvalidOperationException(ReadString(root, "error") ?? "Extension bash execution failed.");
        DispatchUiActions(root); DispatchMessageActions(root, path);
        return root.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number ? exitCode.GetInt32() : null;
    }
}
