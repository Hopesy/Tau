using Tau.Ai;
using Tau.Ai.Streaming;

namespace Tau.AgentCore;

/// <summary>
/// 管理 Agent 在调用方未显式传入 stream 函数时使用的默认 provider 流。
/// </summary>
public static class StreamDefaults
{
    private static Func<Model, LlmContext, SimpleStreamOptions, AssistantMessageStream>? _stream;

    /// <summary>
    /// 设置全局默认 stream 函数；传入 null 可清除配置。
    /// </summary>
    /// <param name="stream">模型、上下文和选项到事件流的函数。</param>
    public static void SetDefaultStreamFn(Func<Model, LlmContext, SimpleStreamOptions, AssistantMessageStream>? stream) => Interlocked.Exchange(ref _stream, stream);

    /// <summary>
    /// 获取当前默认 stream 函数。
    /// </summary>
    /// <returns>已配置的函数。</returns>
    public static Func<Model, LlmContext, SimpleStreamOptions, AssistantMessageStream> GetDefaultStreamFn() =>
        Volatile.Read(ref _stream) ?? throw new InvalidOperationException("No default stream function configured.");
}
