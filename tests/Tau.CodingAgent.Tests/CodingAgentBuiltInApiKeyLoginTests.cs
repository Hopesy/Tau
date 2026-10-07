// 作者：xxx
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【内置密钥登录】原生选择器可进入内置密钥登录，秘密保存后可由真实请求解析器读取。</summary>
    /// <param name="provider">内置提供方。</param><param name="name">登录方式显示名。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("deepseek", "DeepSeek API key")]
    [InlineData("openai", "OpenAI API key")]
    [InlineData("anthropic", "Anthropic API key")]
    [InlineData("google", "Google API key")]
    [InlineData("nvidia", "NVIDIA API key")]
    [InlineData("mistral", "Mistral API key")]
    [InlineData("github-copilot", "GitHub Copilot token")]
    [InlineData("meta", "Meta Model API key")]
    public async Task BuiltInLogin_UsesSecretPromptAndPersistsKey(string provider, string name)
    {
        using var fixture = new Fixture("builtin-login", "models");
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        var callbacks = new BuiltInKeyLoginCallbacks();
        var router = new CodingAgentCommandRouter(session.Runner, oauthLoginCallbacksFactory: () => callbacks, authSelector: (state, _) =>
        {
            var option = Assert.Single(state.Options, option => option.Provider == provider && option.AuthType == "api_key");
            return Task.FromResult<string?>(option.SelectionKey);
        });
        var result = await router.TryHandleAsync("/login");
        Assert.False(result!.IsError, result.Message);
        Assert.Contains("authenticated successfully", result.Message);
        Assert.Equal($"Enter {name}", Assert.Single(callbacks.SecretPrompts));
        Assert.Equal("synthetic-login-key", new OAuthCredentialStore([Path.Combine(fixture.AgentDirectory, "auth.json")]).LoadEntries()[provider].ApiKey);
        Assert.Equal("synthetic-login-key", fixture.Catalog().AuthResolver.ResolveApiKey(provider));
    }

    /// <summary>【CodingAgent】【登录取消】取消发生于提示前或提示返回后时均不写入凭据。</summary>
    /// <param name="beforePrompt">是否在提示前取消。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuiltInLogin_CancellationNeverPersistsKey(bool beforePrompt)
    {
        using var fixture = new Fixture("builtin-cancel", "models");
        using var cancellation = new CancellationTokenSource();
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        var callbacks = new BuiltInKeyLoginCallbacks { AfterPrompt = cancellation.Cancel };
        var router = new CodingAgentCommandRouter(session.Runner, oauthLoginCallbacksFactory: () => callbacks);
        if (beforePrompt) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.TryHandleAsync("/login deepseek", cancellation.Token));
        Assert.Equal(beforePrompt ? 0 : 1, callbacks.SecretPrompts.Count);
        Assert.DoesNotContain(session.Runner.ListStoredCredentials(), credential => credential.ProviderId == "deepseek");
    }

    /// <summary>【CodingAgent】【认证覆盖】原生扩展声明的 OAuth 认证不会被内置密钥定义补回。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task BuiltInLogin_DoesNotAddApiKeyToNativeOAuthOnlyOverride()
    {
        using var fixture = new Fixture("builtin-override", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "override.js"), """
            export default pi=>pi.registerProvider({id:'anthropic',name:'Custom Anthropic',
              getModels:()=>[{id:'chat',provider:'anthropic',name:'Chat',api:'anthropic-messages',baseUrl:'https://custom.invalid'}],
              auth:{oauth:{name:'Account',login:async()=>({type:'oauth',access:'a',refresh:'r',expires:Date.now()+100000}),
                refresh:async c=>c,toAuth:async c=>({apiKey:c.access})}}});
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.NotNull(session.Runner.GetOAuthProvider("anthropic"));
        Assert.Null(session.Runner.GetApiKeyProvider("anthropic"));
        Assert.Null(session.Runner.GetApiKeyProvider("openai-codex"));
        Assert.NotNull(session.Runner.GetApiKeyProvider("github-copilot"));
    }

    /// <summary>【CodingAgent】【密钥输入夹具】只接受秘密输入，普通输入和授权网址均导致测试失败。</summary>
    private sealed class BuiltInKeyLoginCallbacks : IOAuthLoginCallbacks
    {
        public List<string> SecretPrompts { get; } = [];
        public Action? AfterPrompt { get; init; }
        /// <summary>拒绝非秘密输入。</summary><param name="message">提示。</param><param name="placeholder">占位。</param><param name="allowEmpty">是否允许空值。</param><returns>不会返回。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) => throw new InvalidOperationException("Expected secret input.");
        /// <summary>记录秘密输入类型并返回专用模拟密钥。</summary><param name="message">提示。</param><param name="placeholder">占位。</param><param name="token">取消信号。</param><returns>模拟密钥。</returns>
        public Task<string> OnSecretPromptAsync(string message, string? placeholder, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SecretPrompts.Add(message);
            AfterPrompt?.Invoke();
            return Task.FromResult("synthetic-login-key");
        }
        /// <summary>拒绝 API key 登录中的 OAuth 授权链接。</summary><param name="url">链接。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => throw new InvalidOperationException("Unexpected OAuth flow.");
        /// <summary>忽略不影响此测试的普通通知。</summary><param name="message">通知。</param>
        public void OnProgress(string message) { }
        /// <summary>此夹具没有后台验证码输入。</summary><returns>空任务。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
    }
}
