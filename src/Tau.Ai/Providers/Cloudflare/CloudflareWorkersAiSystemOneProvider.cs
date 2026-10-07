// 作者：xxx
using Tau.Ai.Providers.SystemOne;

namespace Tau.Ai.Providers.Cloudflare;

/// <summary>【AI】【Cloudflare 分类】Workers AI REST 分类入口，支持 Jev 与 Clef 响应信封。</summary>
public sealed class CloudflareWorkersAiSystemOneProvider : IClassifierProvider
{
    private readonly SystemOneClient _client;

    /// <summary>创建 Cloudflare 分类传输。</summary>
    /// <param name="httpClient">可选 HTTP 客户端。</param>
    public CloudflareWorkersAiSystemOneProvider(HttpClient? httpClient = null) => _client = new SystemOneClient(httpClient, cloudflare: true);

    /// <inheritdoc />
    public string Api => _client.Api;

    /// <summary>发送账户范围内的分类请求，并解析直接或嵌套输出。</summary>
    /// <param name="model">分类模型。</param>
    /// <param name="context">状态及问题。</param>
    /// <param name="options">包含已解析账户环境的请求选项。</param>
    /// <returns>分类答案、错误或取消结果。</returns>
    public Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions options) =>
        _client.ClassifyAsync(model, context, options);
}
