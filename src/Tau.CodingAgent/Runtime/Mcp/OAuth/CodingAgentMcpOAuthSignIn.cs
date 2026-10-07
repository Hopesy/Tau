// 作者：xxx
// 【MCP】【OAuth 移植】参考 pi 与 MCP TypeScript SDK，原始 MIT 许可见同目录 LICENSE.txt
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime.Mcp.OAuth;

/// <summary>【CodingAgent】【MCP 登录交互】宿主负责展示或打开授权地址，并提供手工粘贴回调地址的备用输入。</summary>
public interface ICodingAgentMcpSignInPrompt
{
    /// <summary>【CodingAgent】【授权地址展示】显示授权地址，宿主可按交互模式打开浏览器。</summary><param name="url">授权 URL。</param><param name="token">取消。</param><returns>展示任务。</returns>
    Task ShowAuthorizationUrlAsync(Uri url, CancellationToken token);
    /// <summary>【CodingAgent】【手工回调输入】浏览器无法连接本机时输入完整回调 URL，浏览器回调先完成时取消等待。</summary><param name="token">取消输入等待。</param><returns>完整 URL 或取消时的空值。</returns>
    Task<string?> PromptForRedirectUrlAsync(CancellationToken token);
}

/// <summary>【CodingAgent】【MCP 显式登录】协调回环监听、动态注册、浏览器或手工回调与令牌保存。</summary>
public static class CodingAgentMcpOAuthSignIn
{
    /// <summary>【CodingAgent】【MCP 登录入口】每次生成独立 state，回调变化时废弃旧注册，并始终关闭回环监听。</summary>
    /// <param name="serverUrl">MCP 地址。</param><param name="store">服务器状态。</param><param name="settings">已解析设置。</param><param name="prompt">宿主交互。</param>
    /// <param name="challenge">最近认证质询。</param><param name="httpClient">宿主管理的 HTTP 客户端。</param><param name="token">用户取消。</param><returns>凭据保存完成任务。</returns>
    public static async Task SignInAsync(string serverUrl, ICodingAgentMcpOAuthStateStore store, CodingAgentMcpOAuthSettings settings,
        ICodingAgentMcpSignInPrompt prompt, CodingAgentMcpOAuthChallenge? challenge = null, HttpClient? httpClient = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var server = CodingAgentMcpOAuthCredentialStore.NormalizeUrl(serverUrl);
        var stored = await store.LoadAsync(token).ConfigureAwait(false);
        if (CodingAgentMcpOAuthJson.Text(stored, "serverUrl") != server) stored = null;
        var registered = CodingAgentMcpOAuthJson.List(stored?["clientInformation"] as JsonObject, "redirect_uris");
        var callback = settings.Callback();
        var preferred = callback.Port ?? (registered.FirstOrDefault() is { } previous && Uri.TryCreate(previous, UriKind.Absolute, out var uri) && !uri.IsDefaultPort ? uri.Port : null);
        var cimd = settings.ClientRegistration == "cimd";
        var callbackOptions = new CodingAgentMcpOAuthCallbackOptions
        {
            Host = callback.Host, RedirectHost = callback.RedirectHost, Path = callback.Path, Port = preferred ?? 0,
            ExtraPaths = cimd ? ["/callback/" + CodingAgentMcpOAuthSettings.CallbackId(server)] : [], RenderPage = RenderPage
        };
        CodingAgentMcpOAuthCallbackServer listener;
        try { listener = CodingAgentMcpOAuthCallbackServer.Listen(callbackOptions); }
        catch (SocketException) when (callback.Port is null && preferred is not null)
        { listener = CodingAgentMcpOAuthCallbackServer.Listen(callbackOptions with { Port = 0 }); }
        await using var ownedListener = listener;
        using var ownedHttp = httpClient is null ? TauHttpClientFactory.Create() : null;
        var http = httpClient ?? ownedHttp!;
        var redirect = callback.FixedUrl ?? listener.RedirectUrl;

        // 1. 【MCP】【登录状态更新】客户端注册绑定原回调，改回调或切换 CIMD 时清理旧客户端和授权
        if (stored is not null)
        {
            var next = stored.DeepClone().AsObject(); next.Remove("oauthState");
            var keep = !string.IsNullOrEmpty(settings.ClientId) || (cimd ? stored["clientInformation"] is null : registered.Contains(redirect, StringComparer.Ordinal));
            if (!keep) { next.Remove("clientInformation"); next.Remove("tokens"); next.Remove("tokensExpireAt"); }
            await store.SaveAsync(next, token).ConfigureAwait(false);
        }
        Uri? authorization = null;
        var provider = settings.CreateProvider(server, store, redirect, (url, _) => { authorization = url; return Task.CompletedTask; });
        var stepUp = challenge?.Error == "insufficient_scope";
        var requested = stepUp ? CodingAgentMcpOAuthFlow.StepUpScope(CodingAgentMcpOAuthJson.Text(stored?["tokens"] as JsonObject, "scope"), challenge?.Scope) : challenge?.Scope;
        var scope = string.Join(" ", new[] { settings.Scope, requested }.SelectMany(value => (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal));
        var options = new CodingAgentMcpOAuthFlowOptions
        {
            ServerUrl = new(server), ResourceMetadataUrl = challenge?.ResourceMetadataUrl, AuthorizationServerMetadataUrl = settings.AuthorizationServerMetadataUrl,
            Scope = scope.Length == 0 ? null : scope, SkipRefresh = stepUp
        };
        var flow = new CodingAgentMcpOAuthFlow(http);
        if (await flow.AuthorizeAsync(provider, options, token).ConfigureAwait(false) == CodingAgentMcpOAuthFlowResult.Authorized) return;
        if (authorization is null) throw new InvalidOperationException("OAuth flow did not produce an authorization URL");

        // 2. 【MCP】【登录回调竞速】先注册浏览器等待再展示 URL，避免快速回调落在等待创建之前
        var state = await provider.StateAsync(token).ConfigureAwait(false);
        var effectiveRedirect = new Uri(CodingAgentMcpOAuthCallbackServer.Query(authorization).GetValueOrDefault("redirect_uri") ?? redirect);
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var fromBrowser = listener.WaitAsync(state, effectiveRedirect.AbsolutePath, inputCancellation.Token);
        Task<CodingAgentMcpOAuthCallback>? fromUser = null;
        try
        {
            await prompt.ShowAuthorizationUrlAsync(authorization, token).ConfigureAwait(false);
            fromUser = FromPromptAsync(prompt, state, effectiveRedirect, inputCancellation.Token);
            var winner = await Task.WhenAny(fromBrowser, fromUser).WaitAsync(token).ConfigureAwait(false);
            var response = await winner.ConfigureAwait(false);
            inputCancellation.Cancel();
            await flow.AuthorizeAsync(provider, options with { AuthorizationCode = response.Code, Issuer = response.Issuer }, token).ConfigureAwait(false);
        }
        finally
        {
            inputCancellation.Cancel();
            ObserveFailure(fromBrowser);
            if (fromUser is not null) ObserveFailure(fromUser);
        }
    }

    /// <summary>【CodingAgent】【手工登录响应】要求完整回调的源站、路径和 state 匹配本次登录，拒绝仅粘贴授权码。</summary>
    /// <param name="input">用户输入。</param><param name="state">预期随机值。</param><param name="redirect">精确回调。</param><returns>授权响应。</returns>
    public static CodingAgentMcpOAuthCallback ParseRedirect(string input, string state, Uri redirect)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var url)) throw new InvalidDataException("Expected the full redirect URL from the browser address bar");
        if (url.GetLeftPart(UriPartial.Authority) != redirect.GetLeftPart(UriPartial.Authority) || url.AbsolutePath != redirect.AbsolutePath)
            throw new InvalidDataException("The redirect URL does not match this sign-in's redirect URI");
        var query = CodingAgentMcpOAuthCallbackServer.Query(url);
        if (query.GetValueOrDefault("error") is { Length: > 0 } error) throw new InvalidOperationException(query.GetValueOrDefault("error_description") ?? error);
        if (query.GetValueOrDefault("state") != state) throw new InvalidDataException("The redirect URL belongs to a different sign-in");
        if (query.GetValueOrDefault("code") is not { Length: > 0 } code) throw new InvalidDataException("The redirect URL does not contain an authorization code");
        return new(code, state, query.GetValueOrDefault("iss"));
    }
    /// <summary>【CodingAgent】【手工登录等待】把空输入转换为用户取消，其余输入经过完整身份校验。</summary>
    /// <param name="prompt">宿主输入。</param><param name="state">预期 state。</param><param name="redirect">预期回调。</param><param name="token">取消。</param><returns>授权响应。</returns>
    private static async Task<CodingAgentMcpOAuthCallback> FromPromptAsync(ICodingAgentMcpSignInPrompt prompt, string state, Uri redirect, CancellationToken token)
    {
        var input = await prompt.PromptForRedirectUrlAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input)) throw new OperationCanceledException("MCP sign-in cancelled", token);
        return ParseRedirect(input, state, redirect);
    }
    /// <summary>【CodingAgent】【登录页面】转义服务端错误文字并提供简洁浏览器反馈。</summary><param name="page">授权结果。</param><returns>HTML。</returns>
    private static string RenderPage(CodingAgentMcpOAuthCallbackPage page) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>MCP sign-in</title></head><body><main><h1>" +
        (page.Ok ? "Signed in" : "Sign-in failed") + "</h1><p>" + WebUtility.HtmlEncode(page.Message) + "</p>" +
        (page.Details is null ? "" : "<pre>" + WebUtility.HtmlEncode(page.Details) + "</pre>") + "</main></body></html>";
    /// <summary>【CodingAgent】【竞速任务清理】观察未获胜输入任务的异常，避免等待不响应取消的宿主输入。</summary><param name="task">剩余任务。</param>
    private static void ObserveFailure(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
