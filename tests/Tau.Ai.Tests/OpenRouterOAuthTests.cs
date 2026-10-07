// 作者：xxx
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Auth.OAuth.Providers;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

/// <summary>【AI】【OpenRouter 测试】使用模拟兑换服务和真实本地回调验证 OAuth 协议。</summary>
public sealed class OpenRouterOAuthTests
{
    /// <summary>【AI】【OpenRouter 测试】手动输入经过 PKCE 兑换，长期密钥刷新不发起网络请求。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ManualLogin_UsesPkceAndPermanentKey()
    {
        using var handler = new ExchangeHandler(); using var client = new HttpClient(handler);
        var provider = Create(client); var callbacks = new LoginCallbacks("https://127.0.0.1/remote?code=remote%2Bcode");
        var credential = await provider.LoginAsync(callbacks);
        Assert.Equal("test-permanent-key", credential.Access);
        Assert.Equal("", credential.Refresh);
        Assert.False(credential.IsExpired());
        Assert.Equal(DateTimeOffset.MaxValue, credential.ExpiresAt);
        Assert.Equal(9007199254740991L, OAuthCredentialJson.Write(credential).GetProperty("expires").GetInt64());
        Assert.Same(credential, await provider.RefreshTokenAsync(credential));
        Assert.Equal("test-permanent-key", provider.GetApiKey(credential));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("remote+code", handler.Body.GetProperty("code").GetString());
        Assert.Equal("S256", handler.Body.GetProperty("code_challenge_method").GetString());
        var query = OAuthLoopbackServer<string>.Query(new Uri(callbacks.AuthUrl!).Query);
        Assert.False(query.ContainsKey("state"));
        Assert.Equal("S256", query["code_challenge_method"]);
        var verifier = handler.Body.GetProperty("code_verifier").GetString()!;
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(challenge, query["code_challenge"]);
        var redirect = new Uri(query["callback_url"]);
        Assert.True(redirect.Port > 0);
        Assert.Equal("127.0.0.1", redirect.Host);
        Assert.StartsWith("/oauth/callback/", redirect.AbsolutePath);
        Assert.True(Guid.TryParse(redirect.Segments[^1], out _));
        Assert.Equal(redirect.AbsoluteUri, callbacks.Placeholder);
        Assert.True(callbacks.AllowEmpty);
        Assert.True(callbacks.PromptToken.IsCancellationRequested);
        Assert.Contains(callbacks.Progress, text => text.Contains("Exchanging authorization code", StringComparison.Ordinal));
    }

    /// <summary>【AI】【OpenRouter 测试】SDK 登录完成后关闭手动输入，授权链接保持结构化通知。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ModelsLogin_BrowserCallbackCancelsManualPrompt()
    {
        using var handler = new ExchangeHandler(); using var client = new HttpClient(handler);
        var configuration = new ModelConfigurationStore([]);
        var resolver = new ProviderAuthResolver(new OAuthProviderRegistry([Create(client)]), new OAuthCredentialStore([]), configurationStore: configuration);
        var definition = BuiltInProviders.CreateBuiltInModels(configuration, resolver).GetProvider("openrouter")!.Auth!;
        Assert.NotNull(definition.ApiKey);
        Assert.Equal("Sign in with OpenRouter", definition.OAuth!.LoginLabel);
        Assert.False(definition.OAuth.IsSubscription);
        var interaction = new BrowserInteraction();
        var login = definition.OAuth.LoginAsync(interaction);
        var prompt = await interaction.Prompt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var notification = Assert.Single(interaction.Notifications, item => item.Type == "auth_url");
        var redirect = OAuthLoopbackServer<string>.Query(new Uri(notification.Url!).Query)["callback_url"];
        Assert.Equal(redirect, prompt.Placeholder);
        Assert.True(prompt.AllowEmpty);
        using var browser = new HttpClient();
        using var response = await browser.GetAsync(redirect + "?code=browser-code");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var credential = await login.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("test-permanent-key", credential.Access);
        Assert.True(prompt.Signal.IsCancellationRequested);
        Assert.Equal("test-permanent-key", (await definition.OAuth.ToAuthAsync(credential)).ApiKey);
    }

    /// <summary>【AI】【OpenRouter 测试】浏览器认领后手动输入到达也只兑换一次。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task CallbackClaim_PreventsManualDoubleExchange()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ExchangeHandler { BeforeResponse = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        using var client = new HttpClient(handler); var callbacks = new LoginCallbacks(null);
        var login = Create(client).LoginAsync(callbacks);
        await callbacks.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var browser = new HttpClient();
        var request = browser.GetAsync(callbacks.Placeholder + "?code=browser-first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callbacks.Manual.SetResult("manual-second");
        Assert.False(login.IsCompleted);
        release.SetResult();
        using var response = await request;
        Assert.Equal("test-permanent-key", (await login).Access);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("browser-first", handler.Body.GetProperty("code").GetString());
    }

    /// <summary>【AI】【OpenRouter 测试】错误响应、非对象 JSON 与缺失密钥返回确定诊断。</summary>
    /// <param name="status">HTTP 状态。</param><param name="body">响应。</param><param name="message">诊断。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(400, "{\"error_description\":\"detail\",\"message\":\"other\"}", "OpenRouter OAuth key exchange failed (HTTP 400): detail")]
    [InlineData(401, "{\"message\":\"unauthorized\"}", "OpenRouter OAuth key exchange failed (HTTP 401): unauthorized")]
    [InlineData(403, "{\"error\":\"denied\"}", "OpenRouter OAuth key exchange failed (HTTP 403): denied")]
    [InlineData(429, "{\"error\":{\"message\":\"slow down\"}}", "OpenRouter OAuth key exchange failed (HTTP 429): slow down")]
    [InlineData(500, "not json", "OpenRouter OAuth key exchange failed (HTTP 500)")]
    [InlineData(200, "not json", "OpenRouter OAuth returned invalid JSON")]
    [InlineData(200, "[]", "OpenRouter OAuth response carries no \"key\"")]
    [InlineData(200, "null", "OpenRouter OAuth response carries no \"key\"")]
    [InlineData(200, "{}", "OpenRouter OAuth response carries no \"key\"")]
    [InlineData(200, "{\"key\":2}", "OpenRouter OAuth response carries no \"key\"")]
    [InlineData(200, "{\"key\":\"\"}", "OpenRouter OAuth response carries no \"key\"")]
    public async Task ExchangeErrors_AreReported(int status, string body, string message)
    {
        using var handler = new ExchangeHandler { Status = status, ResponseBody = body }; using var client = new HttpClient(handler);
        var callbacks = new LoginCallbacks("code");
        Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(callbacks))).Message);
        Assert.True(callbacks.PromptToken.IsCancellationRequested);
    }

    /// <summary>【AI】【OpenRouter 测试】不同输入形式按上游规则读取首个授权码。</summary>
    /// <param name="input">输入。</param><param name="code">预期代码。</param>
    [Theory]
    [InlineData("  raw-code  ", "raw-code")]
    [InlineData("raw#code", "raw#code")]
    [InlineData("?code=first%2Bvalue&code=second", "first+value")]
    [InlineData("state=s&code=hello+world", "hello world")]
    [InlineData("custom://callback?code=custom", "custom")]
    [InlineData("https://localhost/?other=value", null)]
    [InlineData("", null)]
    public void AuthorizationInput_MatchesUpstream(string input, string? code) => Assert.Equal(code, OpenRouterOAuthProvider.ParseAuthorizationInput(input));

    /// <summary>【AI】【OpenRouter 测试】空输入不调用兑换服务。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task MissingCode_DoesNotExchange()
    {
        using var handler = new ExchangeHandler(); using var client = new HttpClient(handler);
        Assert.Equal("Missing authorization code", (await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).LoginAsync(new LoginCallbacks("  ")))).Message);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>【AI】【OpenRouter 测试】登录超时取消提示，兑换超时使用独立的期限诊断。</summary>
    /// <param name="manual">是否手动提交代码触发兑换。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadlines_CancelPendingWork(bool manual)
    {
        using var handler = new ExchangeHandler { BeforeResponse = token => Task.Delay(Timeout.Infinite, token) }; using var client = new HttpClient(handler);
        var callbacks = new LoginCallbacks(manual ? "code" : null);
        var provider = new OpenRouterOAuthProvider(client, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(40), "127.0.0.1");
        var error = await Assert.ThrowsAsync<TimeoutException>(() => provider.LoginAsync(callbacks));
        Assert.Equal(manual ? "OpenRouter OAuth token exchange timed out" : "OpenRouter sign-in timed out", error.Message);
        Assert.True(callbacks.PromptToken.IsCancellationRequested);
    }

    /// <summary>【AI】【OpenRouter 测试】取消已开始的兑换会结束登录，保持取消语义。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task Cancellation_DuringExchangePropagates()
    {
        using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ExchangeHandler { BeforeResponse = token => { entered.TrySetResult(); return Task.Delay(Timeout.Infinite, token); } };
        using var client = new HttpClient(handler);
        var login = Create(client).LoginAsync(new LoginCallbacks("code"), source.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.Cancel();
        Assert.Equal("Login cancelled", (await Assert.ThrowsAsync<OperationCanceledException>(() => login)).Message);
    }

    /// <summary>【AI】【OpenRouter 测试】输入异常终止等待并保留原错误。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ManualPromptFailure_Propagates()
    {
        using var handler = new ExchangeHandler(); using var client = new HttpClient(handler);
        var callbacks = new LoginCallbacks(null);
        var login = Create(client).LoginAsync(callbacks);
        var failure = new InvalidOperationException("input failed"); callbacks.Manual.SetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => login));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>【AI】【OpenRouter 测试】内置注册表直接暴露账户登录和自定义文案。</summary>
    [Fact]
    public void Registry_ContainsOpenRouter()
    {
        var provider = new OAuthProviderRegistry().Get("openrouter");
        Assert.IsType<OpenRouterOAuthProvider>(provider);
        Assert.Equal("OpenRouter OAuth", provider.Name);
        Assert.True(provider.UsesCallbackServer);
        Assert.False(provider.IsSubscription);
    }

    /// <summary>【AI】【OpenRouter 测试】创建无外网依赖的认证实例。</summary><param name="client">模拟客户端。</param><returns>提供方。</returns>
    private static OpenRouterOAuthProvider Create(HttpClient client) => new(client, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), "127.0.0.1");

    /// <summary>【AI】【OpenRouter 测试】记录请求并模拟兑换响应。</summary>
    private sealed class ExchangeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int Status { get; init; } = 200;
        public string ResponseBody { get; init; } = "{\"key\":\"test-permanent-key\"}";
        public JsonElement Body { get; private set; }
        public Func<CancellationToken, Task>? BeforeResponse { get; init; }
        /// <summary>【AI】【OpenRouter 测试】核验 URL、方法和 JSON 请求头后记录授权参数。</summary>
        /// <param name="request">请求。</param><param name="token">取消。</param><returns>模拟响应。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("https://openrouter.ai/api/v1/auth/keys", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "application/json");
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token)); Body = json.RootElement.Clone();
            if (BeforeResponse is not null) await BeforeResponse(token);
            return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(ResponseBody) };
        }
    }

    /// <summary>【AI】【OpenRouter 测试】提供可控手动输入并记录进度和独立取消信号。</summary>
    private sealed class LoginCallbacks(string? input) : IOAuthLoginCallbacks
    {
        public TaskCompletionSource<string> Manual { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PromptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? AuthUrl { get; private set; }
        public string? Placeholder { get; private set; }
        public bool AllowEmpty { get; private set; }
        public CancellationToken PromptToken { get; private set; }
        public List<string> Progress { get; } = [];
        /// <summary>记录授权地址。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => AuthUrl = url;
        /// <summary>记录进度。</summary><param name="message">消息。</param>
        public void OnProgress(string message) => Progress.Add(message);
        /// <summary>此认证不使用旧手动输入接口。</summary><returns>空。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
        /// <summary>禁止丢弃提示取消信号。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">空值策略。</param><returns>输入。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new NotSupportedException();
        /// <summary>记录提示并等待脚本输入。</summary><param name="message">提示。</param><param name="placeholder">占位符。</param><param name="allowEmpty">空值策略。</param><param name="token">信号。</param><returns>输入。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder, bool allowEmpty, CancellationToken token)
        {
            Placeholder = placeholder; AllowEmpty = allowEmpty; PromptToken = token; PromptStarted.TrySetResult();
            return input is not null ? Task.FromResult(input) : Manual.Task.WaitAsync(token);
        }
    }

    /// <summary>【AI】【OpenRouter 测试】记录 Models SDK 类型化交互。</summary>
    private sealed class BrowserInteraction : AuthInteraction
    {
        public List<ProviderAuthNotification> Notifications { get; } = [];
        public TaskCompletionSource<ProviderAuthPrompt> Prompt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken CancellationToken => default;
        /// <summary>旧文本输入不应被调用。</summary><param name="prompt">提示。</param><returns>输入。</returns>
        public Task<string> PromptAsync(string prompt) => throw new NotSupportedException();
        /// <summary>等待独立取消信号关闭输入。</summary><param name="prompt">提示。</param><returns>输入。</returns>
        public async Task<string> PromptAsync(ProviderAuthPrompt prompt) { Prompt.TrySetResult(prompt); await Task.Delay(Timeout.Infinite, prompt.Signal); return ""; }
        /// <summary>旧文本通知不应被调用。</summary><param name="message">消息。</param>
        public void Notify(string message) => throw new NotSupportedException();
        /// <summary>保留类型化通知。</summary><param name="notification">通知。</param>
        public void Notify(ProviderAuthNotification notification) => Notifications.Add(notification);
    }
}
