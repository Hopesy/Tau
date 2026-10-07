// 作者：xxx
using Tau.Ai.Providers.SystemOne;

namespace Tau.Ai.Providers.TypeSafe;

/// <summary>【AI】【System One】TypeSafe、OpenCode 和 Vercel 的分类协议入口。</summary>
public sealed class TypeSafeSystemOneProvider : IClassifierProvider
{
    private readonly SystemOneClient _client;

    /// <summary>创建共享 System One 客户端。</summary>
    /// <param name="httpClient">可选 HTTP 客户端。</param>
    public TypeSafeSystemOneProvider(HttpClient? httpClient = null) => _client = new SystemOneClient(httpClient);

    /// <inheritdoc />
    public string Api => _client.Api;

    /// <summary>执行 TypeSafe 兼容协议的分类请求。</summary>
    /// <param name="model">分类模型。</param>
    /// <param name="context">状态及问题。</param>
    /// <param name="options">请求配置。</param>
    /// <returns>分类答案、错误或取消结果。</returns>
    public Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions options) =>
        _client.ClassifyAsync(model, context, options);
}
