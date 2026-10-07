// 作者：xxx
namespace Tau.Ai.Providers.Mistral;

public sealed partial class MistralProvider
{
    /// <summary>【Mistral】【请求地址】根地址和代理前缀追加主线 v1 路径，同时兼容已有以 v1 结尾的 Tau 配置。</summary>
    /// <param name="baseUrl">模型地址，未设置时使用官方根地址。</param><returns>完整聊天地址，不携带基址查询或片段。</returns>
    private static Uri BuildEndpoint(string? baseUrl)
    {
        var address = new UriBuilder(baseUrl ?? "https://api.mistral.ai");
        var path = address.Path.TrimEnd('/');
        address.Path = path.EndsWith("/v1", StringComparison.Ordinal) ? path + "/chat/completions" : path + "/v1/chat/completions";
        address.Query = "";
        address.Fragment = "";
        return address.Uri;
    }

    /// <summary>【Mistral】【HTTP 错误】清理响应正文并限制为主线的 4000 字符，空正文使用状态说明。</summary>
    /// <param name="response">HTTP 响应。</param><param name="body">原始错误正文。</param><returns>包含状态码的错误信息。</returns>
    private static string FormatHttpError(HttpResponseMessage response, string body)
    {
        var text = body.Trim();
        if (text.Length > 4000) text = text[..4000] + $"... [truncated {text.Length - 4000} chars]";
        if (text.Length == 0) text = string.IsNullOrEmpty(response.ReasonPhrase) ? $"Request failed with status {(int)response.StatusCode}" : response.ReasonPhrase;
        return $"Mistral API error ({(int)response.StatusCode}): {text}";
    }
}
