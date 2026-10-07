// 作者：xxx
namespace Tau.Ai.Providers.Google;

public sealed partial class GoogleProvider
{
    /// <summary>【Google】【请求地址】已配置的版本或代理路径直接追加模型资源，兼容旧根地址默认 v1beta。</summary>
    /// <param name="model">模型及基址。</param><returns>流式生成地址。</returns>
    private static Uri BuildEndpoint(Model model)
    {
        var endpoint = new UriBuilder(model.BaseUrl ?? "https://generativelanguage.googleapis.com/v1beta");
        var path = endpoint.Path.TrimEnd('/');
        if (path.Length == 0) path = "/v1beta";
        var resource = model.Id.StartsWith("models/", StringComparison.Ordinal) ? model.Id : "models/" + model.Id;
        endpoint.Path = path + "/" + resource + ":streamGenerateContent";
        endpoint.Query = "alt=sse";
        endpoint.Fragment = "";
        return endpoint.Uri;
    }
}
