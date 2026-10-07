// 作者：xxx
using Tau.Ai.Auth;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentCommandRouter
{
    /// <summary>【CodingAgent】【登录选项】按提供方展开全部认证方式，持久凭据状态优先于即时有效性。</summary>
    /// <returns>按显示名称排序的认证选项。</returns>
    private IReadOnlyList<CodingAgentAuthOption> GetLoginOptions()
    {
        var stored = _runner.ListStoredCredentials().ToDictionary(credential => credential.ProviderId, StringComparer.OrdinalIgnoreCase);
        var options = new List<CodingAgentAuthOption>();
        foreach (var provider in _runner.GetAuthProviders())
        {
            var oauth = _runner.GetOAuthProvider(provider);
            var apiKey = _runner.GetApiKeyProvider(provider);
            if (oauth is null && apiKey is null) continue;
            var status = stored.TryGetValue(provider, out var credential)
                ? new ProviderAuthStatus(provider, true, "stored credential", credential.Type == "oauth", true, "Stored credential.")
                : _runner.GetAuthStatus(provider);
            var name = _runner.GetProviderDisplayName(provider);
            if (oauth is not null) options.Add(new(provider, name, "oauth", status, oauth.Name, oauth.IsSubscription) { LoginLabel = oauth.LoginLabel });
            if (apiKey is not null) options.Add(new(provider, name, "api_key", status, apiKey.Name, oauth?.IsSubscription == true));
        }
        return options.OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>【CodingAgent】【认证状态】同时提供旧提供方列表和新的逐认证方式列表。</summary>
    /// <param name="options">认证选项。</param><param name="mode">选择器动作。</param><returns>不可变选择状态。</returns>
    private CodingAgentAuthSelectorState CreateAuthState(IReadOnlyList<CodingAgentAuthOption> options, string mode) =>
        new(_runner.Model.Provider, options.Select(option => option.Status ?? _runner.GetAuthStatus(option.Provider))
            .DistinctBy(status => status.Provider, StringComparer.OrdinalIgnoreCase).ToArray()) { Options = options, Mode = mode, SelectMethodFirst = mode == "login" };

    /// <summary>【CodingAgent】【认证选择】解析完整选择值；旧回调只有在提供方方式唯一时才能回退。</summary>
    /// <param name="options">本次允许的选项。</param><param name="selection">选择器结果。</param><returns>唯一选项；伪造或有歧义时为空。</returns>
    private static CodingAgentAuthOption? ResolveAuthSelection(IReadOnlyList<CodingAgentAuthOption> options, string selection)
    {
        var exact = options.FirstOrDefault(option => option.SelectionKey == selection);
        if (exact is not null) return exact;
        var matches = options.Where(option => option.Provider.Equals(selection, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
