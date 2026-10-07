// 作者：xxx
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【认证输入】不回显、也不写入普通输入历史的控制台秘密输入。</summary>
internal static class CodingAgentSecretInput
{
    /// <summary>【CodingAgent】【认证输入】读取秘密文本，支持退格、取消和重定向输入。</summary>
    /// <param name="token">输入取消信号。</param><returns>输入内容。</returns>
    internal static async Task<string> ReadAsync(CancellationToken token) =>
        await TuiConsoleInput.ReadLineAsync(secret: true, token).ConfigureAwait(false) ?? throw new OperationCanceledException("Login cancelled.");
}
