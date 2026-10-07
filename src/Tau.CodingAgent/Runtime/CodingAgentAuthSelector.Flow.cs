// 作者：xxx
namespace Tau.CodingAgent.Runtime;

public static partial class CodingAgentAuthSelector
{
    private const string MethodMenuProvider = "__auth_method__";

    /// <summary>【CodingAgent】【登录导航】先选认证方式，再选提供方；返回键回到方式菜单。</summary>
    /// <param name="state">完整认证列表。</param><param name="choose">显示一个界面的宿主回调。</param><param name="token">取消信号。</param>
    /// <returns>提供方与方式的选择值，关闭顶层菜单时为空。</returns>
    private static async Task<string?> SelectFlowAsync(CodingAgentAuthSelectorState state,
        Func<CodingAgentAuthSelectorState, Task<string?>> choose, CancellationToken token)
    {
        if (!state.SelectMethodFirst || state.Mode != "login" || state.Options.Count == 0) return await choose(state).ConfigureAwait(false);
        var singleProvider = state.Options.Select(option => option.Provider).Distinct(StringComparer.Ordinal).Count() == 1;
        var oauth = state.Options.FirstOrDefault(option => option.AuthType == "oauth");
        var menu = new List<CodingAgentAuthOption>();
        if (oauth is not null) menu.Add(new(MethodMenuProvider, singleProvider ? oauth.LoginLabel ?? "Sign in with an account" : "Sign in with an account", "oauth"));
        if (state.Options.Any(option => option.AuthType == "api_key")) menu.Add(new(MethodMenuProvider, "Sign in with an API key", "api_key"));
        var radius = !singleProvider ? state.Options.FirstOrDefault(option => option.Provider == "radius" && option.AuthType == "oauth") : null;
        if (radius is not null) menu.Add(radius with { Name = "Sign in with " + radius.Name });
        var menuState = state with { Options = menu, Mode = "login-method", InitialFilter = null, SelectMethodFirst = false,
            Title = singleProvider ? $"Select authentication method for {state.Options[0].Name}:" : "Select authentication method:" };
        while (true)
        {
            // 1. 【CodingAgent】【登录导航】顶层取消结束选择，Radius 快捷入口直接返回其 OAuth 方式
            token.ThrowIfCancellationRequested();
            var selected = await choose(menuState).ConfigureAwait(false);
            if (selected is null) return null;
            var method = menu.FirstOrDefault(option => option.SelectionKey == selected);
            if (method is null) return null;
            if (method.Provider != MethodMenuProvider) return method.SelectionKey;
            var options = state.Options.Where(option => option.AuthType == method.AuthType).ToArray();
            if (singleProvider && options.Length == 1) return options[0].SelectionKey;
            // 2. 【CodingAgent】【登录导航】提供方列表取消后重新进入方式菜单，不丢失原始候选列表
            var value = await choose(state with { Options = options, SelectMethodFirst = false }).ConfigureAwait(false);
            if (value is not null) return value;
        }
    }
}
