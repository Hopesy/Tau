// 作者：xxx
using System.Runtime.ExceptionServices;

namespace Tau.Ai.Auth.OAuth;

/// <summary>【AI】【授权输入】浏览器回调和可取消手工输入共用的竞争流程及授权码解析。</summary>
internal static class OAuthAuthorizationInput
{
    /// <summary>【AI】【授权输入】等待浏览器或手工结果，返回前取消剩余提示并观察所有输入错误。</summary>
    /// <param name="callbacks">登录交互。</param><param name="callback">可选回调服务器。</param>
    /// <param name="message">手工提示。</param><param name="placeholder">回调占位地址。</param><param name="token">流程取消。</param>
    /// <returns>回调代码或手工文本，二者只会有一个非空引用。</returns>
    internal static async Task<(string? CallbackCode, string? ManualInput)> WaitAsync(IOAuthLoginCallbacks callbacks,
        OAuthLoopbackServer<string>? callback, string message, string placeholder, CancellationToken token)
    {
        using var manualCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var manual = ReadAsync(callbacks, callback, message, placeholder, manualCancellation.Token);
        try
        {
            var code = callback is null ? null : await callback.WaitAsync().WaitAsync(token).ConfigureAwait(false);
            if (manual.IsCompletedSuccessfully && manual.Result.Error is { } earlyError) ExceptionDispatchInfo.Capture(earlyError).Throw();
            if (code is not null) return (code, null);
            var result = await manual.WaitAsync(token).ConfigureAwait(false);
            if (result.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            return (null, result.Input ?? "");
        }
        finally { await manualCancellation.CancelAsync().ConfigureAwait(false); }
    }

    /// <summary>【AI】【授权输入】手工成功和错误均接管尚未认领的浏览器等待，异步失败不会悬空。</summary>
    /// <param name="callbacks">交互。</param><param name="callback">回调服务器。</param><param name="message">提示。</param>
    /// <param name="placeholder">占位。</param><param name="token">提示取消。</param><returns>输入或异常。</returns>
    private static async Task<(string? Input, Exception? Error)> ReadAsync(IOAuthLoginCallbacks callbacks,
        OAuthLoopbackServer<string>? callback, string message, string placeholder, CancellationToken token)
    {
        try
        {
            var input = await callbacks.OnManualCodeInputAsync(message, placeholder, token).WaitAsync(token).ConfigureAwait(false);
            callback?.Cancel(); return (input, null);
        }
        catch (Exception error) { callback?.Cancel(); return (null, error); }
    }

    /// <summary>【AI】【授权码解析】支持任意绝对 URL、code#state、查询串与裸代码，重复字段采用首项。</summary>
    /// <param name="input">授权输入。</param><returns>代码与可选 state。</returns>
    internal static (string? Code, string? State) Parse(string input)
    {
        var value = input.Trim();
        if (value.Length == 0) return (null, null);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var fields = OAuthLoopbackServer<string>.Query(uri.Query);
            return (fields.GetValueOrDefault("code"), fields.GetValueOrDefault("state"));
        }
        if (value.Contains('#')) { var parts = value.Split('#'); return (parts[0], parts[1]); }
        if (value.Contains("code=", StringComparison.Ordinal))
        {
            var fields = OAuthLoopbackServer<string>.Query(value);
            return (fields.GetValueOrDefault("code"), fields.GetValueOrDefault("state"));
        }
        return (value, null);
    }
}
