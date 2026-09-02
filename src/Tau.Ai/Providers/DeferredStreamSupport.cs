using Tau.Ai.Streaming;

namespace Tau.Ai.Providers;

/// <summary>
/// 为未实现 deferred 协议的 provider 创建一致的错误流。
/// </summary>
internal static class DeferredStreamSupport
{
    /// <summary>
    /// 创建 deferred 拉取不受支持的错误流。
    /// </summary>
    /// <param name="model">原始请求模型。</param>
    /// <param name="handle">待拉取的句柄。</param>
    /// <returns>已结束的错误流。</returns>
    public static AssistantMessageStream UnsupportedFetch(Model model, DeferredHandle handle)
    {
        var stream = new AssistantMessageStream();
        var error = new AssistantMessage
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Content = [],
            StopReason = StopReason.Error,
            ErrorMessage = $"Provider '{model.Provider}' does not support deferred responses for handle '{handle.Id}'.",
            Timestamp = DateTimeOffset.UtcNow
        };
        stream.Push(new ErrorEvent(error.ErrorMessage!, Message: error));
        return stream;
    }
}
