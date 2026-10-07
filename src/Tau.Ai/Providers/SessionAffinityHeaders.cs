// 作者：xxx
namespace Tau.Ai.Providers;

/// <summary>【AI】【会话亲和】按上游协议选择会话路由请求头。</summary>
internal static class SessionAffinityHeaders
{
    /// <summary>【AI】【会话亲和】写入协议默认头；调用方随后可应用模型和请求头覆盖。</summary>
    /// <param name="request">即将发送的 HTTP 请求。</param><param name="model">目标模型与兼容配置。</param>
    /// <param name="options">包含会话标识的请求选项。</param><param name="api">使用的协议，而非提供方别名。</param>
    internal static void Apply(HttpRequestMessage request, Model model, StreamOptions options, string api)
    {
        if (string.IsNullOrEmpty(options.SessionId)) return;
        var isOpenRouter = model.Provider == "openrouter" || model.BaseUrl?.Contains("openrouter.ai", StringComparison.Ordinal) == true;
        var responses = api == "openai-responses";
        var anthropic = api == "anthropic-messages";
        // 1. 【AI】【发送条件】Responses 始终附带会话标识，另外两种协议默认只为 OpenRouter 开启
        if (!responses && !(model.Compat?.SendSessionAffinityHeaders ?? isOpenRouter)) return;
        var format = model.Compat?.SessionAffinityFormat ?? (isOpenRouter ? "openrouter" : "openai");
        if (format == "openrouter")
        {
            Set(request, "x-session-id", options.SessionId);
            return;
        }
        // 2. 【AI】【协议差异】Anthropic 仅发送亲和头，Responses 不发送亲和头，Completions 发送完整组合
        if (!anthropic)
        {
            if (format == "openai") Set(request, "session_id", options.SessionId);
            Set(request, "x-client-request-id", options.SessionId);
        }
        if (!responses) Set(request, "x-session-affinity", options.SessionId);
    }

    /// <summary>【AI】【请求头赋值】替换同名头并保留调用方提供的会话字符串。</summary>
    /// <param name="request">请求对象。</param><param name="name">头名称。</param><param name="value">会话值。</param>
    private static void Set(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }
}
