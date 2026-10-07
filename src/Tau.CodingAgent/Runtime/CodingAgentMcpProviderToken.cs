// 作者：xxx
using Tau.Ai;
using Tau.Ai.Providers;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【MCP 会话令牌】读取当前提供方的有效密钥，沿用 API key/OAuth 解析而不转发模型专属地址或请求头。</summary>
    /// <param name="provider">MCP 配置引用的提供方。</param><param name="token">取消信号。</param><returns>令牌，缺失或解析失败返回空值。</returns>
    internal Task<string?> ResolveMcpProviderTokenAsync(string provider, CancellationToken token) => Task.Run(() =>
    {
        try
        {
            var model = new Model { Id = string.Empty, Name = provider, Provider = provider, Api = string.Empty };
            return StreamFunctions.ResolveRequestAuthentication(model, new StreamOptions
            {
                Signal = token,
                ApiKey = string.Equals(provider, Model.Provider, StringComparison.OrdinalIgnoreCase) ? _config.StreamOptions?.ApiKey : null
            }, _modelCatalog.ConfigurationStore, _authResolver).ApiKey;
        }
        catch (Exception) when (!token.IsCancellationRequested) { return null; }
    }, token);
}
