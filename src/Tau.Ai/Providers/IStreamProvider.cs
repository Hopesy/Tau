using Tau.Ai.Streaming;

namespace Tau.Ai.Providers;

/// <summary>
/// Each LLM provider implements this interface.
/// Stream functions must not throw — errors are delivered as stream events.
/// </summary>
public interface IStreamProvider
{
    string Api { get; }

    /// <summary>是否自行处理系统声明；旧实现默认由统一入口合并提示和工具。</summary>
    bool SupportsTranscriptContext => false;

    AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options);

    AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options);

    /// <summary>
    /// 获取 provider 已提交的 deferred 响应。
    /// </summary>
    /// <param name="model">原始请求模型。</param>
    /// <param name="handle">provider 返回的 deferred 句柄。</param>
    /// <param name="options">拉取选项。</param>
    /// <returns>包含最终消息或错误的事件流。</returns>
    AssistantMessageStream FetchDeferred(Model model, DeferredHandle handle, DeferredFetchOptions options) =>
        DeferredStreamSupport.UnsupportedFetch(model, handle);

    /// <summary>
    /// 尝试取消 provider 已提交的 deferred 响应。
    /// </summary>
    /// <param name="model">原始请求模型。</param>
    /// <param name="handle">provider 返回的 deferred 句柄。</param>
    /// <param name="options">取消选项。</param>
    /// <returns>取消完成任务。</returns>
    Task CancelDeferred(Model model, DeferredHandle handle, DeferredCancelOptions options) =>
        Task.FromException(new NotSupportedException($"Provider '{model.Provider}' does not support deferred cancellation."));
}
